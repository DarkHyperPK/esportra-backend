using System.Data;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Competitive;
using Esportra.Contracts.Database;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Agreeing a scrim's time. A scrim is created on accept with no time; either captain/manager
/// proposes one, the other accepts or declines, and proposing again counters.
///
/// POST /api/scrims/sessions/{id}/time-proposals   — propose (supersedes any pending proposal)
/// POST /api/scrims/time-proposals/{id}/accept     — the OTHER team accepts: sets the match time
/// POST /api/scrims/time-proposals/{id}/decline    — the OTHER team declines
/// </summary>
public static class ScrimScheduleEndpoints
{
    public static void MapScrimScheduleEndpoints(this WebApplication app)
    {
        app.MapPost("/api/scrims/sessions/{id:guid}/time-proposals", ProposeAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/time-proposals/{id:guid}/accept", AcceptAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/time-proposals/{id:guid}/decline", DeclineAsync).RequireAuthorization("Authenticated");
    }

    private sealed record ProposeTimeRequest(
        [property: JsonPropertyName("proposed_at")] DateTime ProposedAt,
        [property: JsonPropertyName("note")] string? Note = null);

    private sealed record SessionTimeRow
    {
        public Guid Team1Id { get; init; }
        public Guid Team2Id { get; init; }
        public string Status { get; init; } = null!;
        public DateTime? Team1CheckedInAt { get; init; }
        public DateTime? Team2CheckedInAt { get; init; }
    }

    private sealed record ProposalRow
    {
        public Guid Id { get; init; }
        public Guid SessionId { get; init; }
        public Guid ProposedByTeamId { get; init; }
        public DateTime ProposedAt { get; init; }
        public string Status { get; init; } = null!;
        public Guid Team1Id { get; init; }
        public Guid Team2Id { get; init; }
        public string SessionStatus { get; init; } = null!;
        public DateTime? Team1CheckedInAt { get; init; }
        public DateTime? Team2CheckedInAt { get; init; }
    }

    /// <summary>The captain/manager's team among the two, or null if they staff neither.</summary>
    private static async Task<Guid?> StaffTeamAsync(IDbConnection conn, Guid userId, params Guid[] teamIds) =>
        await conn.ExecuteScalarAsync<Guid?>(
            """
            SELECT team_id FROM public.team_members
            WHERE user_id = @userId AND team_id = ANY(@teamIds)
              AND role IN ('captain', 'manager') AND is_active = TRUE
            LIMIT 1
            """,
            new { userId, teamIds });

    /// <summary>409 message when the time can no longer change, else null.</summary>
    private static string? Frozen(string status, DateTime? t1CheckedIn, DateTime? t2CheckedIn) =>
        status != "scheduled" ? $"The time can't be changed while the scrim is {status.Replace('_', ' ')}."
        : t1CheckedIn is not null || t2CheckedIn is not null ? "Check-in has started, so the time can't change now."
        : null;

    private static async Task<IResult> ProposeAsync(
        Guid id, [FromBody] ProposeTimeRequest req, HttpContext ctx, IDbConnectionFactory db,
        IHubContext<NotificationHub> hub, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();

        var proposedAt = req.ProposedAt.ToUniversalTime();
        var error = ScrimScheduling.ValidateProposedTime(proposedAt, DateTime.UtcNow);
        if (error is not null) return Results.UnprocessableEntity(new { error });
        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        if (note?.Length > 200) return Results.UnprocessableEntity(new { error = "Note must be 200 characters or fewer." });

        using var conn = db.CreateConnection();
        var session = await conn.QuerySingleOrDefaultAsync<SessionTimeRow>(
            """
            SELECT team1_id, team2_id, status::text AS status, team1_checked_in_at, team2_checked_in_at
            FROM public.scrim_sessions WHERE id = @id
            """,
            new { id });
        if (session is null) return Results.NotFound();

        var myTeam = await StaffTeamAsync(conn, user.UserIdGuid, session.Team1Id, session.Team2Id);
        if (myTeam is null) return Results.Forbid();

        var frozen = Frozen(session.Status, session.Team1CheckedInAt, session.Team2CheckedInAt);
        if (frozen is not null) return Results.Conflict(new { error = frozen });

        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(
            "UPDATE public.scrim_time_proposals SET status = 'superseded', responded_at = now() WHERE session_id = @id AND status = 'pending'",
            new { id }, tx);
        var proposal = await conn.QuerySingleAsync<ScrimTimeProposalRow>(
            """
            INSERT INTO public.scrim_time_proposals (session_id, proposed_by_team_id, proposed_at, note)
            VALUES (@id, @myTeam, @proposedAt, @note)
            RETURNING id, session_id, proposed_by_team_id, proposed_at, note, status, created_at
            """,
            new { id, myTeam, proposedAt, note }, tx);
        tx.Commit();

        var otherTeam = myTeam == session.Team1Id ? session.Team2Id : session.Team1Id;
        await ScrimTeamNotifier.SendAsync(conn, hub, otherTeam, id, "scrim_time_proposed",
            "Match time proposed", "The other team proposed a time for your scrim. Accept it or suggest another.");
        return Results.Ok(proposal);
    }

    private static async Task<ProposalRow?> LoadProposalAsync(IDbConnection conn, Guid id) =>
        await conn.QuerySingleOrDefaultAsync<ProposalRow>(
            """
            SELECT p.id, p.session_id, p.proposed_by_team_id, p.proposed_at, p.status,
                   s.team1_id, s.team2_id, s.status::text AS session_status,
                   s.team1_checked_in_at, s.team2_checked_in_at
            FROM public.scrim_time_proposals p
            JOIN public.scrim_sessions s ON s.id = p.session_id
            WHERE p.id = @id
            """,
            new { id });

    /// <summary>Only the team that did NOT propose may respond.</summary>
    private static async Task<(IResult? Failure, ProposalRow? Proposal, Guid ResponderTeam)> AuthorizeResponseAsync(
        IDbConnection conn, Guid id, UserContext user)
    {
        var p = await LoadProposalAsync(conn, id);
        if (p is null) return (Results.NotFound(), null, Guid.Empty);
        var responder = p.ProposedByTeamId == p.Team1Id ? p.Team2Id : p.Team1Id;
        if (await StaffTeamAsync(conn, user.UserIdGuid, responder) is null)
            return (Results.Forbid(), null, Guid.Empty);
        if (p.Status != "pending")
            return (Results.Conflict(new { error = "This proposal has already been answered or replaced." }), null, Guid.Empty);
        return (null, p, responder);
    }

    private static async Task<IResult> AcceptAsync(
        Guid id, HttpContext ctx, IDbConnectionFactory db, IHubContext<NotificationHub> hub,
        IBackgroundJobClient jobs, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();

        var (failure, p, _) = await AuthorizeResponseAsync(conn, id, user);
        if (failure is not null || p is null) return failure!;

        var frozen = Frozen(p.SessionStatus, p.Team1CheckedInAt, p.Team2CheckedInAt);
        if (frozen is not null) return Results.Conflict(new { error = frozen });
        if (ScrimScheduling.ValidateProposedTime(p.ProposedAt, DateTime.UtcNow) is not null)
            return Results.Conflict(new { error = "That time has passed. Propose a new one." });

        using var tx = conn.BeginTransaction();
        var accepted = await conn.ExecuteScalarAsync<Guid?>(
            """
            UPDATE public.scrim_time_proposals
            SET status = 'accepted', responded_at = now(), responded_by = @userId
            WHERE id = @id AND status = 'pending' RETURNING id
            """,
            new { id, userId = user.UserIdGuid }, tx);
        if (accepted is null)
        {
            tx.Rollback();
            return Results.Conflict(new { error = "This proposal has already been answered or replaced." });
        }
        // Rescheduling restarts check-in: the window job opens it again for the new time.
        await conn.ExecuteAsync(
            """
            UPDATE public.scrim_sessions
            SET scheduled_at = @at, checkin_window_opened_at = NULL, updated_at = now()
            WHERE id = @sessionId
            """,
            new { at = p.ProposedAt, sessionId = p.SessionId }, tx);
        tx.Commit();

        await ScrimJobScheduler.ScheduleAsync(conn, jobs, p.SessionId, p.ProposedAt);
        await ScrimTeamNotifier.SendAsync(conn, hub, p.ProposedByTeamId, p.SessionId, "scrim_time_accepted",
            "Match time confirmed", "The other team accepted your proposed time. Your scrim is scheduled.");
        return Results.Ok(new { proposal_id = id, session_id = p.SessionId, scheduled_at = p.ProposedAt });
    }

    private static async Task<IResult> DeclineAsync(
        Guid id, HttpContext ctx, IDbConnectionFactory db, IHubContext<NotificationHub> hub, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();

        var (failure, p, _) = await AuthorizeResponseAsync(conn, id, user);
        if (failure is not null || p is null) return failure!;

        var declined = await conn.ExecuteScalarAsync<Guid?>(
            """
            UPDATE public.scrim_time_proposals
            SET status = 'declined', responded_at = now(), responded_by = @userId
            WHERE id = @id AND status = 'pending' RETURNING id
            """,
            new { id, userId = user.UserIdGuid });
        if (declined is null)
            return Results.Conflict(new { error = "This proposal has already been answered or replaced." });

        await ScrimTeamNotifier.SendAsync(conn, hub, p.ProposedByTeamId, p.SessionId, "scrim_time_declined",
            "Match time declined", "The other team can't make that time. Propose another.");
        return Results.Ok(new { proposal_id = id, session_id = p.SessionId });
    }
}
