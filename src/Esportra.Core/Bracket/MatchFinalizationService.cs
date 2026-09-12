using Dapper;
using Esportra.Contracts.Database;
using Esportra.Core.Notifications;
using Esportra.Core.Tournaments;
using Microsoft.Extensions.Logging;

namespace Esportra.Core.Bracket;

/// <summary>Groups the result parameters passed to a match finalization call.</summary>
public sealed record FinalizeMatchOptions(
    Guid WinnerId,
    Guid? LoserId = null,
    int? Team1Score = null,
    int? Team2Score = null);

/// <summary>
/// Replaces the PostgreSQL <c>finalize_match_locked()</c> RPC function.
/// Handles pessimistic locking, version check, score update, bracket advancement,
/// and event logging — all in .NET with Dapper.
/// </summary>
public sealed class MatchFinalizationService(
    IDbConnectionFactory db,
    IEnumerable<ILeaderboardSourceChangeHook> leaderboardHooks,
    ILogger<MatchFinalizationService> logger)
{
    /// <summary>
    /// Acquires a pessimistic row lock on the match, validates the version, and finalizes
    /// the match in its own connection and transaction. Leaderboard hooks fire after commit.
    /// </summary>
    /// <returns>True if finalized; false if match not found.</returns>
    /// <exception cref="InvalidOperationException">Version mismatch (concurrent modification).</exception>
    public async Task<bool> FinalizeAsync(
        Guid matchId,
        int expectedVersion,
        FinalizeMatchOptions options,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var locked = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM public.brkt_matches WHERE id = @matchId FOR UPDATE)",
                new { matchId }, tx);
            if (!locked) return false;

            var finalized = await FinalizeInTransactionAsync(matchId, expectedVersion, options, conn, tx, ct);
            tx.Commit();

            if (finalized)
                await LeaderboardSourceChangeHooks.FireAsync(leaderboardHooks, logger, nameof(MatchFinalizationService), ct);
            return finalized;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Finalizes a match within an already-open transaction.
    /// Use this when the caller holds a FOR UPDATE lock on the match row — avoids the
    /// application-level deadlock that occurs when a second connection tries to acquire
    /// the same row lock while the first connection is still open.
    ///
    /// The caller is responsible for committing or rolling back the transaction,
    /// and for firing leaderboard hooks after a successful commit.
    /// </summary>
    /// <exception cref="InvalidOperationException">Version mismatch (concurrent modification).</exception>
    public async Task<bool> FinalizeInTransactionAsync(
        Guid matchId,
        int expectedVersion,
        FinalizeMatchOptions options,
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        CancellationToken ct = default)
    {
        // Skip FOR UPDATE — the caller's transaction already holds the row lock.
        var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT version FROM public.brkt_matches WHERE id = @matchId",
            new { matchId }, tx);

        if (match is null) return false;

        if ((int)match.version != expectedVersion)
            throw new InvalidOperationException(
                $"Match version mismatch. Expected {expectedVersion}, got {(int)match.version}.");

        await conn.ExecuteAsync(
            """
            UPDATE public.brkt_matches
            SET winner_id   = @winnerId,
                loser_id    = @loserId,
                team1_score = COALESCE(@team1Score, team1_score),
                team2_score = COALESCE(@team2Score, team2_score),
                status      = 'completed',
                version     = version + 1,
                updated_at  = NOW()
            WHERE id = @matchId
            """,
            new
            {
                matchId,
                winnerId = options.WinnerId,
                loserId = (object?)options.LoserId ?? DBNull.Value,
                team1Score = options.Team1Score,
                team2Score = options.Team2Score,
            },
            tx);

        await AdvanceTeamInternalAsync(conn, tx, matchId, options.WinnerId, "winner");
        if (options.LoserId.HasValue)
            await AdvanceTeamInternalAsync(conn, tx, matchId, options.LoserId.Value, "loser");

        await conn.ExecuteAsync(
            """
            INSERT INTO public.match_completed_events (match_id, winner_id, loser_id, status)
            VALUES (@matchId, @winnerId, @loserId, 'processed')
            """,
            new
            {
                matchId,
                winnerId = options.WinnerId,
                loserId = (object?)options.LoserId ?? DBNull.Value,
            },
            tx);

        return true;
    }

    /// <summary>
    /// Simplified overload for cases where version is unknown (e.g., organizer force-finalize).
    /// Fetches current version automatically.
    /// </summary>
    public async Task<bool> FinalizeAsync(
        Guid matchId,
        FinalizeMatchOptions options,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var version = await conn.QuerySingleOrDefaultAsync<int?>(
            "SELECT version FROM public.brkt_matches WHERE id = @matchId",
            new { matchId });

        if (version is null) return false;

        return await FinalizeAsync(matchId, version.Value, options, ct);
    }

    private static async Task AdvanceTeamInternalAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        Guid sourceMatchId,
        Guid teamId,
        string edgeType)
    {
        var sourceMatch = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT team1_id, team2_id, team1_seed, team2_seed FROM public.brkt_matches WHERE id = @sourceMatchId",
            new { sourceMatchId }, tx);

        int? teamSeed = null;
        if (sourceMatch is not null)
        {
            if ((Guid?)sourceMatch.team1_id == teamId)
                teamSeed = (int?)sourceMatch.team1_seed;
            else if ((Guid?)sourceMatch.team2_id == teamId)
                teamSeed = (int?)sourceMatch.team2_seed;
        }

        var edges = (await conn.QueryAsync(
            """
            SELECT target_match_id, target_slot
            FROM public.brkt_advancements
            WHERE source_match_id = @sourceMatchId AND type = @edgeType
            """,
            new { sourceMatchId, edgeType }, tx)).AsList();

        foreach (var edge in edges)
        {
            if ((int)edge.target_slot == 1)
                await conn.ExecuteAsync(
                    "UPDATE public.brkt_matches SET team1_id = @teamId, team1_seed = @teamSeed WHERE id = @targetId",
                    new { teamId, teamSeed, targetId = (Guid)edge.target_match_id }, tx);
            else
                await conn.ExecuteAsync(
                    "UPDATE public.brkt_matches SET team2_id = @teamId, team2_seed = @teamSeed WHERE id = @targetId",
                    new { teamId, teamSeed, targetId = (Guid)edge.target_match_id }, tx);

            await NotifyIfMatchReadyAsync(conn, tx, (Guid)edge.target_match_id);
        }
    }

    /// <summary>
    /// Replaces the <c>notify_match_ready()</c> DB trigger.
    /// If both team slots are filled, notifies captains of both teams.
    /// </summary>
    private static async Task NotifyIfMatchReadyAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        Guid matchId)
    {
        var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT team1_id, team2_id FROM public.brkt_matches WHERE id = @matchId",
            new { matchId }, tx);

        if (match is null || match.team1_id is null || match.team2_id is null)
            return;

        var teamIds = new[] { (Guid)match.team1_id, (Guid)match.team2_id };
        var captainIds = (await conn.QueryAsync<Guid>(
            """
            SELECT tm.user_id FROM public.team_members tm
            WHERE tm.team_id = ANY(@teamIds) AND tm.role = 'captain' AND tm.is_active = true
            """,
            new { teamIds }, tx)).AsList();

        if (captainIds.Count == 0)
        {
            captainIds = (await conn.QueryAsync<Guid>(
                """
                SELECT user_id FROM public.tournament_participants
                WHERE id = ANY(@teamIds) AND user_id IS NOT NULL
                """,
                new { teamIds }, tx)).AsList();
        }

        var matchContext = await CaptainMatchLinkBuilder.ResolveContextAsync(conn, matchId, tx);
        var matchLink = CaptainMatchLinkBuilder.BuildLink(matchContext.TournamentSlug, matchId);

        foreach (var captainId in captainIds)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, link, data, is_read)
                VALUES (@userId, 'match_ready', 'Match Ready',
                        'Your match is ready. Head to the Captain dashboard to start the map veto.',
                        @link,
                        jsonb_build_object(
                            'match_id', @matchId::text,
                            'tournament_slug', @tournamentSlug
                        )::jsonb,
                        false)
                """,
                new
                {
                    userId = captainId,
                    matchId,
                    link = matchLink,
                    tournamentSlug = matchContext.TournamentSlug,
                },
                tx);
        }
    }
}
