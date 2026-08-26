using Dapper;
using Esportra.Contracts.Database;
using Esportra.Core.Notifications;

namespace Esportra.Core.Bracket;

/// <summary>
/// Replaces the PostgreSQL <c>finalize_match_locked()</c> RPC function.
/// Handles pessimistic locking, version check, score update, bracket advancement,
/// and event logging — all in .NET with Dapper.
/// </summary>
public sealed class MatchFinalizationService(IDbConnectionFactory db)
{

    /// <summary>
    /// Replaces the PostgreSQL <c>finalize_match_locked()</c> RPC function.
    /// Handles pessimistic locking, version check, score update, bracket advancement,
    /// and event logging — all in .NET with Dapper.
    /// </summary>
    /// <returns>True if finalized successfully; false if match not found.</returns>
    /// <exception cref="InvalidOperationException">Version mismatch (concurrent modification).</exception>
    public async Task<bool> FinalizeAsync(
        Guid matchId,
        int expectedVersion,
        Guid winnerId,
        Guid? loserId,
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
                new { matchId, winnerId, loserId = (object?)loserId ?? DBNull.Value, team1Score, team2Score },
                tx);

            // 4. Advance teams through bracket edges
            await AdvanceTeamInternalAsync(conn, tx, matchId, winnerId, "winner");
            if (loserId.HasValue)
                await AdvanceTeamInternalAsync(conn, tx, matchId, loserId.Value, "loser");

            // 5. Log completion event (audit trail)
            try
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO public.match_completed_events (match_id, winner_id, loser_id, status)
                    VALUES (@matchId, @winnerId, @loserId, 'processed')
                    """,
                    new { matchId, winnerId, loserId = (object?)loserId ?? DBNull.Value },
                    tx);
            }
            catch { /* Non-critical if loser_id FK fails on audit table */ }

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
        Guid? loserId,
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
        // Fetch source match to get team seeds
        var sourceMatch = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT team1_id, team2_id, team1_seed, team2_seed FROM public.brkt_matches WHERE id = @sourceMatchId",
            new { sourceMatchId },
            tx);

        // Determine the seed of the advancing team
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
            new { sourceMatchId, edgeType },
            tx)).AsList();

        foreach (var edge in edges)
        {
            string teamCol = (int)edge.target_slot == 1 ? "team1_id" : "team2_id";
            string seedCol = (int)edge.target_slot == 1 ? "team1_seed" : "team2_seed";
            await conn.ExecuteAsync(
                $"UPDATE public.brkt_matches SET {teamCol} = @teamId, {seedCol} = @teamSeed WHERE id = @targetId",
                new { teamId, teamSeed, targetId = (Guid)edge.target_match_id },
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

        var teamIds = new[] { (Guid)match.team1_id, (Guid)match.team2_id };
        var captainIds = (await conn.QueryAsync<Guid>(
            """
            SELECT tm.user_id FROM public.team_members tm
            WHERE tm.team_id = ANY(@teamIds) AND tm.role = 'captain' AND tm.is_active = true
            """,
            new { teamIds }, tx)).AsList();

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
