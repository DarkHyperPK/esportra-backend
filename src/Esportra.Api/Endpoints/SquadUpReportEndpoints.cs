using System.Data;
using Dapper;
using Esportra.Api.Middleware;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Alerts;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Squad Up player reports (reporter side).
/// POST   /api/competitive/lfg/swipe/reports        — multipart: reason, details, proof link, up to 5 screenshots
/// GET    /api/competitive/lfg/swipe/reports/mine   — the reporter's own reports (status only)
/// GET    /api/competitive/lfg/swipe/blocks         — players the reporter hid
/// DELETE /api/competitive/lfg/swipe/blocks/{id}    — unhide a player
/// </summary>
public static class SquadUpReportEndpoints
{
    private const string Base = "/api/competitive/lfg/swipe";

    public static void MapSquadUpReportEndpoints(this WebApplication app)
    {
        app.MapPost($"{Base}/reports", SubmitAsync)
            .RequireAuthorization("Authenticated")
            .DisableAntiforgery()
            .WithMetadata(new RateLimitPolicyMetadata("strict"));
        app.MapGet($"{Base}/reports/mine", MineAsync).RequireAuthorization("Authenticated");
        app.MapGet($"{Base}/blocks", BlocksAsync).RequireAuthorization("Authenticated");
        app.MapDelete($"{Base}/blocks/{{userId:guid}}", UnblockAsync).RequireAuthorization("Authenticated");
    }

    private static async Task<IResult> SubmitAsync(
        HttpContext ctx, IDbConnectionFactory db, IReportProofStore store, AdminAlertService alerts, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext me) return Results.Unauthorized();
        if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "NOT_MULTIPART" });
        var form = await ctx.Request.ReadFormAsync(ct);
        var (draft, draftError) = SquadUpReportRules.ParseDraft(form, me.UserIdGuid);
        if (draft is null) return Results.BadRequest(new { error = draftError });
        var (images, imageError) = await SquadUpReportRules.ReadImagesAsync(form.Files.GetFiles("files"), ct);
        if (imageError is not null) return Results.BadRequest(new { error = imageError });

        using var conn = db.CreateConnection();
        var blocked = await CheckGateAsync(conn, me.UserIdGuid, draft);
        if (blocked is not null) return blocked;

        var reportId = Guid.NewGuid();
        var stored = await StoreImagesAsync(store, reportId, images, ct);
        if (stored is null) return Results.Json(new { error = "STORAGE_FAILED" }, statusCode: StatusCodes.Status502BadGateway);
        try
        {
            await PersistAsync(conn, me.UserIdGuid, draft, reportId, stored);
        }
        catch
        {
            await store.DeleteAsync(stored.Select(s => s.Path).ToList(), CancellationToken.None);
            throw;
        }
        await alerts.CreateAsync("squad_up_report", AlertSeverity.Warning, "Squad Up player reported",
            $"A player was reported for {draft.Reason.Replace('_', ' ')}.", new { report_id = reportId, reason = draft.Reason }, ct);
        return Results.Created($"{Base}/reports/{reportId}", new { id = reportId });
    }

    private static async Task<IResult?> CheckGateAsync(IDbConnection conn, Guid myId, ReportDraft draft)
    {
        var gate = await conn.QuerySingleAsync<(bool Connected, bool Duplicate, int Recent)>(
            $"""
            SELECT (EXISTS ({LfgSwipeSql.PoolTarget()})
                    OR EXISTS (SELECT 1 FROM public.lfg_swipe_matches m
                               WHERE m.user1_id = LEAST(@myId, @targetId) AND m.user2_id = GREATEST(@myId, @targetId)
                                 AND m.matched_at > now() - interval '7 days')
                    OR EXISTS ({SharedLobbySql})) AS connected,
                   EXISTS (SELECT 1 FROM public.squad_up_reports r
                           WHERE r.reporter_id = @myId AND r.reported_id = @targetId AND r.created_at > now() - interval '24 hours') AS duplicate,
                   (SELECT count(*) FROM public.squad_up_reports r
                    WHERE r.reporter_id = @myId AND r.created_at > now() - interval '1 hour')::int AS recent
            """, new { myId, targetId = draft.ReportedId });
        if (!gate.Connected) return Results.NotFound(new { error = "NOT_REPORTABLE" });
        if (gate.Duplicate) return Results.Conflict(new { error = "ALREADY_REPORTED" });
        if (gate.Recent >= SquadUpReportRules.HourlyLimit) return Results.Json(new { error = "TOO_MANY_REPORTS" }, statusCode: StatusCodes.Status429TooManyRequests);
        return null;
    }

    /// <summary>Both players were in the same lobby (host or member, any request) in the last 7 days. Needs @myId, @targetId.</summary>
    private const string SharedLobbySql =
        """
        SELECT 1 FROM public.squad_up_lobbies sl
        WHERE sl.created_at > now() - interval '7 days'
          AND (sl.host_id = @myId OR EXISTS (SELECT 1 FROM public.squad_up_lobby_members a WHERE a.lobby_id = sl.id AND a.user_id = @myId))
          AND (sl.host_id = @targetId OR EXISTS (SELECT 1 FROM public.squad_up_lobby_members b WHERE b.lobby_id = sl.id AND b.user_id = @targetId))
        """;

    private sealed record StoredImage(string Path, ReportImage Image);

    private static async Task<List<StoredImage>?> StoreImagesAsync(
        IReportProofStore store, Guid reportId, IReadOnlyList<ReportImage> images, CancellationToken ct)
    {
        var stored = new List<StoredImage>();
        foreach (var image in images)
        {
            var path = ReportProofStore.BuildPath(reportId, image.Extension);
            if (!await store.UploadAsync(path, image.Data, image.ContentType, ct))
            {
                await store.DeleteAsync(stored.Select(s => s.Path).ToList(), CancellationToken.None);
                return null;
            }
            stored.Add(new StoredImage(path, image));
        }
        return stored;
    }

    private static async Task PersistAsync(IDbConnection conn, Guid myId, ReportDraft draft, Guid reportId, List<StoredImage> stored)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        var snapshot = await conn.ExecuteScalarAsync<string>(
            """
            SELECT jsonb_build_object('username', p.username,
                       'riot_id', CASE WHEN ra.game_name IS NULL THEN NULL ELSE ra.game_name || '#' || COALESCE(ra.tag_line, '') END,
                       'region', ra.region, 'tier', pgs.competitive_tier, 'tier_name', pgs.competitive_tier_name)::text
            FROM public.profiles p
            LEFT JOIN public.riot_accounts ra ON ra.user_id = p.id
            LEFT JOIN public.player_game_stats pgs ON pgs.user_id = p.id AND pgs.game_slug = 'valorant'
            WHERE p.id = @id
            """, new { id = draft.ReportedId }, tx) ?? "{}";
        var matchId = draft.MatchId is null ? null : await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT id FROM public.lfg_swipe_matches
            WHERE id = @matchId AND user1_id = LEAST(@myId, @targetId) AND user2_id = GREATEST(@myId, @targetId)
            """, new { matchId = draft.MatchId, myId, targetId = draft.ReportedId }, tx);
        var lobbyId = draft.LobbyId is null ? null : await conn.QuerySingleOrDefaultAsync<Guid?>(
            $"SELECT @lobbyId WHERE EXISTS ({SharedLobbySql} AND sl.id = @lobbyId)",
            new { lobbyId = draft.LobbyId, myId, targetId = draft.ReportedId }, tx);
        var context = lobbyId is not null ? "lobby" : matchId is not null ? "match" : "card";
        await conn.ExecuteAsync(
            """
            INSERT INTO public.squad_up_reports (id, reporter_id, reported_id, match_id, lobby_id, context, reason, details, proof_link, snapshot)
            VALUES (@reportId, @myId, @targetId, @matchId, @lobbyId, @context, @reason, @details, @proofLink, @snapshot::jsonb)
            """, new { reportId, myId, targetId = draft.ReportedId, matchId, lobbyId, context, reason = draft.Reason, details = draft.Details, proofLink = draft.ProofLink, snapshot }, tx);
        await conn.ExecuteAsync(
            "INSERT INTO public.squad_up_report_evidence (report_id, storage_path, content_type, size_bytes) VALUES (@reportId, @Path, @ContentType, @Size)",
            stored.Select(s => new { reportId, s.Path, s.Image.ContentType, Size = s.Image.Data.Length }), tx);
        if (draft.Hide)
            await conn.ExecuteAsync(
                "INSERT INTO public.squad_up_blocks (blocker_id, blocked_id, source_report_id) VALUES (@myId, @targetId, @reportId) ON CONFLICT DO NOTHING",
                new { myId, targetId = draft.ReportedId, reportId }, tx);
        // The reported player leaves the reporter's deck right away, whether or not they are hidden for good.
        await conn.ExecuteAsync(
            $"""
            INSERT INTO public.lfg_swipe_actions (session_id, target_user_id, direction)
            SELECT s.id, @targetId, 'pass' FROM public.lfg_swipe_sessions s
            WHERE s.user_id = @myId AND s.game_slug = 'valorant' AND {LfgSwipeSql.Active("s")}
            ON CONFLICT (session_id, target_user_id) DO NOTHING
            """, new { myId, targetId = draft.ReportedId }, tx);
        tx.Commit();
    }

    private static async Task<IResult> MineAsync(HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext me) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<SquadUpMyReportRow>(
            """
            SELECT r.id, r.reason, r.status, r.created_at, r.resolved_at, p.username AS reported_username
            FROM public.squad_up_reports r JOIN public.profiles p ON p.id = r.reported_id
            WHERE r.reporter_id = @myId ORDER BY r.created_at DESC LIMIT 20
            """, new { myId = me.UserIdGuid });
        return Results.Ok(rows);
    }

    private static async Task<IResult> BlocksAsync(HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext me) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<SquadUpBlockRow>(
            """
            SELECT b.blocked_id AS user_id, p.username, b.created_at
            FROM public.squad_up_blocks b JOIN public.profiles p ON p.id = b.blocked_id
            WHERE b.blocker_id = @myId ORDER BY b.created_at DESC LIMIT 100
            """, new { myId = me.UserIdGuid });
        return Results.Ok(rows);
    }

    private static async Task<IResult> UnblockAsync(Guid userId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext me) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var removed = await conn.ExecuteAsync(
            "DELETE FROM public.squad_up_blocks WHERE blocker_id = @myId AND blocked_id = @userId", new { myId = me.UserIdGuid, userId });
        return removed == 0 ? Results.NotFound() : Results.NoContent();
    }
}

public sealed record SquadUpMyReportRow
{
    public Guid Id { get; init; }
    public string Reason { get; init; } = "";
    public string Status { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string? ReportedUsername { get; init; }
}

public sealed record SquadUpBlockRow
{
    public Guid UserId { get; init; }
    public string? Username { get; init; }
    public DateTime CreatedAt { get; init; }
}
