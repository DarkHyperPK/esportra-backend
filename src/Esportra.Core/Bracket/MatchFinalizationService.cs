using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Core.Bracket;

/// <summary>
/// Replaces the PostgreSQL <c>finalize_match_locked()</c> RPC function.
/// Handles pessimistic locking, version check, score update, bracket advancement,
/// and event logging — all in .NET with Dapper.
/// </summary>
public sealed class MatchFinalizationService(IDbConnectionFactory db)
{
    /// <summary>
    /// Atomically finalizes a match: sets winner/loser, advances teams through bracket edges,
    /// and logs the completion event. Uses pessimistic locking (SELECT FOR UPDATE) and
    /// optimistic concurrency (version check).
    /// </summary>
    /// <returns>True if finalized successfully; false if match not found.</returns>
    /// <exception cref="InvalidOperationException">Version mismatch (concurrent modification).</exception>
    public async Task<bool> FinalizeAsync(
        Guid matchId,
        int expectedVersion,
        Guid winnerId,
        Guid loserId,
        int? team1Score = null,
        int? team2Score = null,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();

        try
        {
            // 1. Pessimistic lock — acquire row lock on the match
            var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT m.version, v.tournament_id
                FROM public.brkt_matches m
                JOIN public.brkt_versions v ON m.version_id = v.id
                WHERE m.id = @matchId
                FOR UPDATE
                """,
                new { matchId },
                tx);

            if (match is null) return false;

            int actualVersion = (int)match.version;

            // 2. Optimistic concurrency check
            if (actualVersion != expectedVersion)
                throw new InvalidOperationException(
                    $"Match version mismatch. Expected {expectedVersion}, got {actualVersion}.");

            // 3. Atomic score + status update
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
                new { matchId, winnerId, loserId, team1Score, team2Score },
                tx);

            // 4. Advance teams through bracket edges
            await AdvanceTeamInternalAsync(conn, tx, matchId, winnerId, "winner");
            await AdvanceTeamInternalAsync(conn, tx, matchId, loserId, "loser");

            // 5. Log completion event (audit trail)
            await conn.ExecuteAsync(
                """
                INSERT INTO public.match_completed_events (match_id, winner_id, loser_id, status)
                VALUES (@matchId, @winnerId, @loserId, 'processed')
                """,
                new { matchId, winnerId, loserId },
                tx);

            tx.Commit();
            return true;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Simplified overload for cases where version is unknown (e.g., organizer force-finalize).
    /// Fetches current version automatically.
    /// </summary>
    public async Task<bool> FinalizeAsync(
        Guid matchId,
        Guid winnerId,
        Guid loserId,
        int? team1Score = null,
        int? team2Score = null,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var version = await conn.QuerySingleOrDefaultAsync<int?>(
            "SELECT version FROM public.brkt_matches WHERE id = @matchId",
            new { matchId });

        if (version is null) return false;

        return await FinalizeAsync(matchId, version.Value, winnerId, loserId, team1Score, team2Score, ct);
    }

    private static async Task AdvanceTeamInternalAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        Guid sourceMatchId,
        Guid teamId,
        string edgeType)
    {
        var edges = (await conn.QueryAsync(
            """
            SELECT target_match_id, target_slot
            FROM public.brkt_advancements
            WHERE source_match_id = @sourceMatchId AND type = @edgeType
            """,
            new { sourceMatchId, edgeType },
            tx)).AsList();

        foreach (var edge in edges)
        {
            string col = (int)edge.target_slot == 1 ? "team1_id" : "team2_id";
            await conn.ExecuteAsync(
                $"UPDATE public.brkt_matches SET {col} = @teamId WHERE id = @targetId",
                new { teamId, targetId = (Guid)edge.target_match_id },
                tx);

            // Check if both teams are now assigned → notify captains
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

        var captainIds = (await conn.QueryAsync<Guid>(
            """
            SELECT tm.user_id FROM public.team_members tm
            WHERE tm.team_id IN (@t1, @t2) AND tm.role = 'captain' AND tm.is_active = true
            """,
            new { t1 = (Guid)match.team1_id, t2 = (Guid)match.team2_id }, tx)).AsList();

        foreach (var captainId in captainIds)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, link, data, is_read)
                VALUES (@userId, 'match_ready', 'Match Ready',
                        'Your match is ready. Head to the Captain dashboard to start the map veto.',
                        '/tournaments/captain',
                        jsonb_build_object('match_id', @matchId::text)::jsonb, false)
                """,
                new { userId = captainId, matchId }, tx);
        }
    }
}
