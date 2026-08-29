using Dapper;
using Esportra.Contracts.Database;
using Esportra.Core.Bracket;

namespace Esportra.Core.Tournaments;

/// <summary>
/// Awards walkovers when the check-in window has closed. When neither team checks in,
/// the match is completed as a double forfeit and organizers are notified.
/// Uses the same effective schedule and window rules as <see cref="SelfPlayMatchRoomService"/>.
/// </summary>
public sealed class CheckinWalkoverProcessor(
    IDbConnectionFactory db,
    MatchFinalizationService finalizer)
{
    public async Task<CheckinWalkoverOutcome> TryProcessDueWalkoverAsync(
        Guid matchId,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var row = await conn.QuerySingleOrDefaultAsync<WalkoverMatchRow>(
            """
            SELECT
              m.id              AS MatchId,
              m.status          AS Status,
              m.team1_id        AS Team1Id,
              m.team2_id        AS Team2Id,
              m.best_of         AS BestOf,
              m.scheduled_time  AS MatchScheduledTime,
              ts.scheduling_config::text AS SchedulingConfigJson,
              t.game            AS Game
            FROM brkt_matches m
            JOIN brkt_versions v ON v.id = m.version_id
            JOIN tournament_stages ts ON ts.id = v.stage_id
            JOIN tournaments t ON t.id = v.tournament_id
            WHERE m.id = @matchId
            """,
            new { matchId });

        if (row is null)
            return CheckinWalkoverOutcome.NotApplicable;

        var checkins = (await conn.QueryAsync<Guid>(
            """
            SELECT team_id
            FROM match_checkins
            WHERE match_id = @matchId
            """,
            new { matchId })).ToHashSet();

        var acceptedProposalTime = await conn.QuerySingleOrDefaultAsync<DateTime?>(
            """
            SELECT proposed_time
            FROM match_time_proposals
            WHERE match_id = @matchId AND status = 'accepted'
            ORDER BY responded_at DESC NULLS LAST, created_at DESC
            LIMIT 1
            """,
            new { matchId });

        var schedulingConfig = SchedulingConfigParser.Parse(row.SchedulingConfigJson);
        var ctx = new SelfPlayMatchRoomContext
        {
            MatchId = row.MatchId,
            Status = row.Status ?? "pending",
            MatchScheduledTime = row.MatchScheduledTime,
            AcceptedProposalTime = acceptedProposalTime,
            Team1Id = row.Team1Id,
            Team2Id = row.Team2Id,
            BestOf = row.BestOf,
            SchedulingConfig = schedulingConfig,
            Game = row.Game,
            Team1CheckedIn = row.Team1Id.HasValue && checkins.Contains(row.Team1Id.Value),
            Team2CheckedIn = row.Team2Id.HasValue && checkins.Contains(row.Team2Id.Value),
        };

        return await TryProcessDueWalkoverAsync(conn, ctx, nowUtc, ct);
    }

    /// <summary>
    /// Pure eligibility check shared with tests — no database writes.
    /// </summary>
    public static CheckinWalkoverOutcome EvaluateEligibility(
        SelfPlayMatchRoomContext ctx,
        DateTime nowUtc)
    {
        var status = (ctx.Status ?? "pending").Trim().ToLowerInvariant();
        if (status != "pending")
            return CheckinWalkoverOutcome.NotApplicable;

        if (!ctx.Team1Id.HasValue || !ctx.Team2Id.HasValue)
            return CheckinWalkoverOutcome.NotApplicable;

        var (effectiveTime, _) = SelfPlayMatchRoomService.ResolveEffectiveSchedule(ctx);
        if (!effectiveTime.HasValue)
            return CheckinWalkoverOutcome.WindowNotClosed;

        var windowMinutes = ctx.SchedulingConfig.CheckinWindowMinutes;
        if (!SelfPlayMatchRoomService.IsCheckinWindowClosed(effectiveTime.Value, windowMinutes, nowUtc))
            return CheckinWalkoverOutcome.WindowNotClosed;

        if (ctx.Team1CheckedIn && ctx.Team2CheckedIn)
            return CheckinWalkoverOutcome.BothCheckedIn;

        if (ctx.Team1CheckedIn && !ctx.Team2CheckedIn)
            return CheckinWalkoverOutcome.Walkover(ctx.Team1Id.Value, true, false);

        if (!ctx.Team1CheckedIn && ctx.Team2CheckedIn)
            return CheckinWalkoverOutcome.Walkover(ctx.Team2Id!.Value, false, true);

        // Neither team checked in — both teams forfeit, and organizers are notified.
        return CheckinWalkoverOutcome.DoubleForfeit(false, false);
    }

    internal async Task<CheckinWalkoverOutcome> TryProcessDueWalkoverAsync(
        System.Data.IDbConnection conn,
        SelfPlayMatchRoomContext ctx,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var eligibility = EvaluateEligibility(ctx, nowUtc);
        if (eligibility.Status is CheckinWalkoverStatus.WindowNotClosed
            or CheckinWalkoverStatus.BothCheckedIn
            or CheckinWalkoverStatus.NotApplicable)
            return eligibility;

        var bestOf = ctx.BestOf > 0 ? ctx.BestOf : 1;
        var winnerScore = bestOf == 1 ? 1 : (int)Math.Ceiling(bestOf / 2.0);

        if (ctx.Team1CheckedIn && !ctx.Team2CheckedIn)
        {
            var success = await finalizer.FinalizeAsync(
                ctx.MatchId,
                ctx.Team1Id!.Value,
                ctx.Team2Id,
                winnerScore,
                0,
                ct);

            return success
                ? CheckinWalkoverOutcome.Walkover(ctx.Team1Id.Value, ctx.Team1CheckedIn, ctx.Team2CheckedIn)
                : CheckinWalkoverOutcome.Failed;
        }

        if (!ctx.Team1CheckedIn && ctx.Team2CheckedIn)
        {
            var success = await finalizer.FinalizeAsync(
                ctx.MatchId,
                ctx.Team2Id!.Value,
                ctx.Team1Id,
                0,
                winnerScore,
                ct);

            return success
                ? CheckinWalkoverOutcome.Walkover(ctx.Team2Id.Value, ctx.Team1CheckedIn, ctx.Team2CheckedIn)
                : CheckinWalkoverOutcome.Failed;
        }

        return await MarkDoubleForfeitAsync(conn, ctx, ct)
            ? CheckinWalkoverOutcome.DoubleForfeit(ctx.Team1CheckedIn, ctx.Team2CheckedIn)
            : CheckinWalkoverOutcome.Failed;
    }

    private static async Task<bool> MarkDoubleForfeitAsync(
        System.Data.IDbConnection conn,
        SelfPlayMatchRoomContext ctx,
        CancellationToken ct)
    {
        using var tx = conn.BeginTransaction();
        try
        {
            var locked = await conn.QuerySingleOrDefaultAsync<LockedMatchRow>(
                """
                SELECT id AS Id, LOWER(COALESCE(status, 'pending')) AS Status
                FROM public.brkt_matches
                WHERE id = @matchId
                FOR UPDATE
                """,
                new { matchId = ctx.MatchId },
                tx);

            if (locked is null)
            {
                tx.Rollback();
                return false;
            }

            if (locked.Status == "completed")
            {
                tx.Commit();
                return true;
            }

            await conn.ExecuteAsync(
                """
                UPDATE public.brkt_matches
                SET winner_id = NULL,
                    loser_id = NULL,
                    team1_score = 0,
                    team2_score = 0,
                    status = 'completed',
                    version = version + 1,
                    updated_at = NOW()
                WHERE id = @matchId
                """,
                new { matchId = ctx.MatchId },
                tx);

            // Propagate null teams through advancements so downstream matches aren't stuck waiting
            var advancements = await conn.QueryAsync<dynamic>(
                "SELECT target_match_id, target_slot FROM public.brkt_advancements WHERE source_match_id = @matchId",
                new { matchId = ctx.MatchId }, tx);

            foreach (var adv in advancements)
            {
                var slot = (int)adv.target_slot;
                var teamField = slot == 1 ? "team1_id" : "team2_id";
                var seedField = slot == 1 ? "team1_seed" : "team2_seed";
                await conn.ExecuteAsync(
                    $"UPDATE public.brkt_matches SET {teamField} = NULL, {seedField} = NULL WHERE id = @targetId",
                    new { targetId = (Guid)adv.target_match_id }, tx);
            }

            tx.Commit();
            return true;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<IReadOnlyList<Guid>> FindCandidateMatchIdsAsync(CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        return (await conn.QueryAsync<Guid>(
            """
            SELECT m.id
            FROM public.brkt_matches m
            WHERE LOWER(COALESCE(m.status, 'pending')) = 'pending'
              AND m.team1_id IS NOT NULL
              AND m.team2_id IS NOT NULL
              AND (
                    m.scheduled_time IS NOT NULL
                 OR EXISTS (
                        SELECT 1
                        FROM public.match_time_proposals p
                        WHERE p.match_id = m.id AND p.status = 'accepted'
                    )
              )
            """)).AsList();
    }

    private sealed class LockedMatchRow
    {
        public Guid Id { get; init; }
        public string Status { get; init; } = "pending";
    }

    private sealed class WalkoverMatchRow
    {
        public Guid MatchId { get; init; }
        public string? Status { get; init; }
        public Guid? Team1Id { get; init; }
        public Guid? Team2Id { get; init; }
        public int BestOf { get; init; }
        public DateTime? MatchScheduledTime { get; init; }
        public string? SchedulingConfigJson { get; init; }
        public string? Game { get; init; }
    }
}

public enum CheckinWalkoverStatus
{
    WindowNotClosed,
    BothCheckedIn,
    NotApplicable,
    WalkoverAwarded,
    DoubleForfeit,
    Failed,
}

public sealed record CheckinWalkoverOutcome(
    CheckinWalkoverStatus Status,
    Guid? WinnerId = null,
    bool Team1CheckedIn = false,
    bool Team2CheckedIn = false)
{
    public bool Processed =>
        Status is CheckinWalkoverStatus.WalkoverAwarded or CheckinWalkoverStatus.DoubleForfeit;

    public bool NeedsOrganizerNotification =>
        Status is CheckinWalkoverStatus.DoubleForfeit;

    public static CheckinWalkoverOutcome WindowNotClosed { get; } =
        new(CheckinWalkoverStatus.WindowNotClosed);

    public static CheckinWalkoverOutcome BothCheckedIn { get; } =
        new(CheckinWalkoverStatus.BothCheckedIn);

    public static CheckinWalkoverOutcome NotApplicable { get; } =
        new(CheckinWalkoverStatus.NotApplicable);

    public static CheckinWalkoverOutcome Failed { get; } =
        new(CheckinWalkoverStatus.Failed);

    public static CheckinWalkoverOutcome Walkover(Guid winnerId, bool team1CheckedIn, bool team2CheckedIn) =>
        new(CheckinWalkoverStatus.WalkoverAwarded, winnerId, team1CheckedIn, team2CheckedIn);

    public static CheckinWalkoverOutcome DoubleForfeit(bool team1CheckedIn, bool team2CheckedIn) =>
        new(CheckinWalkoverStatus.DoubleForfeit, null, team1CheckedIn, team2CheckedIn);
}
