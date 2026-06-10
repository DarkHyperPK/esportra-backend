using Dapper;
using Esportra.Api.Hubs;
using Esportra.Contracts.Database;
using Esportra.Core.Bracket;
using Esportra.Core.Notifications;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.BackgroundJobs;

/// <summary>
/// Periodically scans for matches whose check-in window has expired and awards
/// walkovers (single no-show) or double forfeits (both no-show).
///
/// Runs every 60 seconds. A match is eligible for auto-walkover when:
///   NOW() >= scheduled_time + checkin_window_minutes + 5-minute grace period
/// and the match status is still 'pending'.
/// </summary>
public sealed class CheckinWalkoversJob(
    IServiceScopeFactory         scopeFactory,
    IHubContext<MatchHub>        matchHub,
    IHubContext<NotificationHub> notifHub,
    ILogger<CheckinWalkoversJob> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval  = TimeSpan.FromSeconds(60);
    private static readonly int      GraceMinutes  = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait 30s on startup for other services to initialize
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        logger.LogInformation("[CheckinWalkovers] Background job started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessExpiredMatchesAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "[CheckinWalkovers] Error during poll cycle.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessExpiredMatchesAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        var finalizer = scope.ServiceProvider.GetRequiredService<MatchFinalizationService>();

        using var conn = db.CreateConnection();

        // Find pending matches whose check-in window + grace period has expired.
        // Join tournament_stages to read the scheduling_config JSONB for checkin_window_minutes.
        var expiredMatches = (await conn.QueryAsync<ExpiredMatch>(
            """
            SELECT m.id              AS match_id,
                   m.team1_id,
                   m.team2_id,
                   m.best_of,
                   m.scheduled_time,
                   COALESCE(
                       (ts.scheduling_config->>'checkinWindowMinutes')::int,
                       (ts.scheduling_config->>'CheckinWindowMinutes')::int,
                       15
                   ) AS checkin_window_minutes
            FROM   public.brkt_matches m
            JOIN   public.brkt_versions v  ON m.version_id = v.id
            JOIN   public.tournament_stages ts ON v.stage_id = ts.id
            WHERE  m.status = 'pending'
              AND  m.scheduled_time IS NOT NULL
              AND  m.team1_id IS NOT NULL
              AND  m.team2_id IS NOT NULL
              AND  NOW() >= m.scheduled_time
                           + make_interval(mins => COALESCE(
                               (ts.scheduling_config->>'checkinWindowMinutes')::int,
                               (ts.scheduling_config->>'CheckinWindowMinutes')::int,
                               15))
                           + make_interval(mins => @grace)
            """,
            new { grace = GraceMinutes })).AsList();

        if (expiredMatches.Count == 0) return;

        logger.LogInformation("[CheckinWalkovers] Found {Count} expired match(es).", expiredMatches.Count);

        foreach (var match in expiredMatches)
        {
            try
            {
                await HandleExpiredMatchAsync(conn, finalizer, match, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[CheckinWalkovers] Failed to process match {MatchId}.", match.match_id);
            }
        }
    }

    private async Task HandleExpiredMatchAsync(
        System.Data.IDbConnection conn,
        MatchFinalizationService finalizer,
        ExpiredMatch match,
        CancellationToken ct)
    {
        // Check which teams checked in
        var checkins = (await conn.QueryAsync<Guid>(
            "SELECT team_id FROM match_checkins WHERE match_id = @matchId",
            new { matchId = match.match_id })).AsList();

        var t1In = checkins.Contains(match.team1_id);
        var t2In = checkins.Contains(match.team2_id);

        if (t1In && t2In)
            return; // Both checked in — not a walkover scenario

        var bestOf = match.best_of > 0 ? match.best_of : 1;
        var winnerScore = bestOf == 1 ? 1 : (int)Math.Ceiling(bestOf / 2.0);

        if (t1In && !t2In)
        {
            // Team 1 checked in, Team 2 no-show → Team 1 wins
            await AwardWalkoverAsync(finalizer, match.match_id,
                winnerId: match.team1_id, loserId: match.team2_id,
                team1Score: winnerScore, team2Score: 0, ct);

            logger.LogInformation("[CheckinWalkovers] Match {Id}: Team2 no-show → Team1 wins walkover.",
                match.match_id);
        }
        else if (!t1In && t2In)
        {
            // Team 2 checked in, Team 1 no-show → Team 2 wins
            await AwardWalkoverAsync(finalizer, match.match_id,
                winnerId: match.team2_id, loserId: match.team1_id,
                team1Score: 0, team2Score: winnerScore, ct);

            logger.LogInformation("[CheckinWalkovers] Match {Id}: Team1 no-show → Team2 wins walkover.",
                match.match_id);
        }
        else
        {
            // Double forfeit — neither team checked in
            // Award to team1 slot with 0-0 score so bracket advances; next opponent gets a BYE
            await AwardDoubleForfeitAsync(conn, finalizer, match, ct);

            logger.LogInformation("[CheckinWalkovers] Match {Id}: Double forfeit — both eliminated.",
                match.match_id);
        }

        // Notify via SignalR
        await matchHub.Clients
            .Group(MatchHub.MatchGroup(match.match_id.ToString()))
            .SendAsync(MatchHubEvents.StatusChanged,
                new { matchId = match.match_id, status = "completed", reason = "walkover" }, ct);

        // Notify captains of both teams
        await NotifyCaptainsAsync(conn, match, t1In, t2In, ct);
    }

    private static async Task AwardWalkoverAsync(
        MatchFinalizationService finalizer,
        Guid matchId, Guid winnerId, Guid loserId,
        int team1Score, int team2Score,
        CancellationToken ct)
    {
        await finalizer.FinalizeAsync(matchId, winnerId, loserId, team1Score, team2Score, ct);
    }

    /// <summary>
    /// Double forfeit: mark match completed with no real winner.
    /// The match is finalized with team1 as nominal winner (score 0-0) so the bracket
    /// can advance. The next-round opponent effectively gets a BYE because neither
    /// team deserved to advance.
    ///
    /// We set status to 'completed' and leave winner_id NULL so the advancement
    /// slot stays empty — giving the next opponent a BYE.
    /// </summary>
    private async Task AwardDoubleForfeitAsync(
        System.Data.IDbConnection conn,
        MatchFinalizationService finalizer,
        ExpiredMatch match,
        CancellationToken ct)
    {
        // Mark the match as completed with no winner — manual SQL since FinalizeAsync requires a winnerId
        var version = await conn.QuerySingleOrDefaultAsync<int?>(
            "SELECT version FROM public.brkt_matches WHERE id = @matchId",
            new { matchId = match.match_id });

        if (version is null) return;

        await conn.ExecuteAsync(
            """
            UPDATE public.brkt_matches
            SET status      = 'completed',
                team1_score = 0,
                team2_score = 0,
                version     = version + 1,
                updated_at  = NOW()
            WHERE id = @matchId AND version = @version AND status = 'pending'
            """,
            new { matchId = match.match_id, version });

        // Log audit event
        try
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.match_completed_events (match_id, winner_id, loser_id, status)
                VALUES (@matchId, NULL, NULL, 'double_forfeit')
                """,
                new { matchId = match.match_id });
        }
        catch { /* Non-critical audit */ }

        // Advance neither team — next-round slots stay empty (BYE for opponent)
        // No calls to AdvanceTeam since no winner exists
    }

    private async Task NotifyCaptainsAsync(
        System.Data.IDbConnection conn,
        ExpiredMatch match,
        bool t1In, bool t2In,
        CancellationToken ct)
    {
        var teamIds = new[] { match.team1_id, match.team2_id };
        var captains = (await conn.QueryAsync<CaptainInfo>(
            """
            SELECT tm.user_id, tm.team_id
            FROM public.team_members tm
            WHERE tm.team_id = ANY(@teamIds) AND tm.role = 'captain' AND tm.is_active = true
            """,
            new { teamIds })).AsList();

        foreach (var captain in captains)
        {
            var isNoShow = (captain.team_id == match.team1_id && !t1In)
                        || (captain.team_id == match.team2_id && !t2In);

            string title, message;
            if (!t1In && !t2In)
            {
                title = "⚠️ Double Forfeit";
                message = "Neither team checked in. Both teams have been eliminated from this match.";
            }
            else if (isNoShow)
            {
                title = "❌ Match Forfeited";
                message = "Your team missed the check-in window. A walkover has been awarded to your opponent.";
            }
            else
            {
                title = "✅ Walkover Win";
                message = "Your opponent failed to check in. You've been awarded a walkover victory!";
            }

            var matchContext = await CaptainMatchLinkBuilder.ResolveContextAsync(conn, match.match_id);
            var matchLink = CaptainMatchLinkBuilder.BuildLink(matchContext.TournamentSlug, match.match_id);

            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, link, data, is_read)
                VALUES (@userId, 'match_walkover', @title, @message, @link,
                        jsonb_build_object(
                            'match_id', @matchId::text,
                            'tournament_slug', @tournamentSlug
                        )::jsonb,
                        false)
                """,
                new
                {
                    userId = captain.user_id,
                    title,
                    message,
                    link = matchLink,
                    matchId = match.match_id,
                    tournamentSlug = matchContext.TournamentSlug,
                });

            // Push via SignalR
            await notifHub.Clients
                .Group(NotificationHub.UserGroup(captain.user_id.ToString()))
                .SendAsync(NotificationHubEvents.NewNotification,
                    new { type = "match_walkover", title, message }, ct);
        }
    }

    // ── Internal DTOs ─────────────────────────────────────────────────────────

    private sealed class ExpiredMatch
    {
        public Guid match_id { get; init; }
        public Guid team1_id { get; init; }
        public Guid team2_id { get; init; }
        public int  best_of  { get; init; }
        public DateTime scheduled_time { get; init; }
        public int  checkin_window_minutes { get; init; }
    }

    private sealed class CaptainInfo
    {
        public Guid user_id { get; init; }
        public Guid team_id { get; init; }
    }
}
