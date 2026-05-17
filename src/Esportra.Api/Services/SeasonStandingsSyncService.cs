using System.Data;
using Dapper;
using Esportra.Contracts.Database;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Services;

/// <summary>
/// Syncs tournament final placements into season standings when a tournament completes.
/// Maps each team's placement against season_point_rules to award points, then recalculates ranks.
/// </summary>
public sealed class SeasonStandingsSyncService(
    IDbConnectionFactory db,
    ILogger<SeasonStandingsSyncService> logger)
{
    /// <summary>
    /// Called when a tournament transitions to 'completed'. Finds all linked seasons,
    /// resolves team placements, maps them to point rules, and upserts standings.
    /// </summary>
    public async Task SyncTournamentResultsAsync(Guid tournamentId, HybridCache? cache = null, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        await SyncTournamentResultsAsync(tournamentId, conn, null, cache, ct);
    }

    public async Task SyncTournamentResultsAsync(Guid tournamentId, IDbConnection conn, IDbTransaction? tx = null, HybridCache? cache = null, CancellationToken ct = default)
    {
        // 1. Find all seasons linked to this tournament
        var linkedSeasons = (await conn.QueryAsync<LinkedSeasonRow>(
            """
            SELECT st.season_id, st.season_role, s.status AS season_status
            FROM public.season_tournaments st
            JOIN public.seasons s ON s.id = st.season_id AND s.deleted_at IS NULL
            WHERE st.tournament_id = @tournamentId
              AND s.status IN ('published', 'active')
            """,
            new { tournamentId }, tx)).AsList();

        if (linkedSeasons.Count == 0)
        {
            logger.LogDebug("[SeasonSync] Tournament {TournamentId} is not linked to any active season.", tournamentId);
            return;
        }

        // 2. Resolve final placements for teams in this tournament
        var placements = await ResolveTournamentPlacementsAsync(conn, tournamentId);
        if (placements.Count == 0)
        {
            logger.LogWarning("[SeasonSync] Tournament {TournamentId} completed but no team placements could be resolved.", tournamentId);
            return;
        }

        logger.LogInformation("[SeasonSync] Tournament {TournamentId} completed with {Count} placed teams. Syncing to {SeasonCount} season(s).",
            tournamentId, placements.Count, linkedSeasons.Count);

        // 3. For each linked season, apply point rules and upsert standings
        foreach (var season in linkedSeasons)
        {
            try
            {
                var affectedTeams = await ApplyPointsToSeasonAsync(conn, season.SeasonId, tournamentId, placements, ct, tx);

                if (affectedTeams.Count > 0)
                {
                    await RecalculateRanksAsync(conn, season.SeasonId, tx);
                    logger.LogInformation("[SeasonSync] {Count} team standings updated in season {SeasonId}.", affectedTeams.Count, season.SeasonId);
                }
                else
                {
                    logger.LogDebug("[SeasonSync] No point changes for season {SeasonId}; skipping rank recalc.", season.SeasonId);
                }

                // Update last_standings_sync_at
                await conn.ExecuteAsync(
                    "UPDATE public.seasons SET last_standings_sync_at = NOW(), updated_at = NOW() WHERE id = @seasonId",
                    new { seasonId = season.SeasonId }, tx);

                // Invalidate cache
                if (cache is not null)
                {
                    try { await cache.RemoveAsync($"season:{season.SeasonId}", ct); } catch { }
                    try { await cache.RemoveAsync($"season-detail:{season.SeasonId}", ct); } catch { }
                }

                logger.LogInformation("[SeasonSync] Successfully synced standings for season {SeasonId}.", season.SeasonId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[SeasonSync] Failed to sync standings for season {SeasonId} from tournament {TournamentId}.",
                    season.SeasonId, tournamentId);
            }
        }
    }

    /// <summary>
    /// Full recalculation: re-reads ALL linked tournament results for a season and rebuilds standings from scratch.
    /// </summary>
    public async Task RecalculateSeasonStandingsAsync(Guid seasonId, HybridCache? cache = null, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        // 1. Get all completed tournaments linked to this season
        var tournaments = (await conn.QueryAsync<Guid>(
            """
            SELECT st.tournament_id
            FROM public.season_tournaments st
            JOIN public.tournaments t ON t.id = st.tournament_id
            WHERE st.season_id = @seasonId
              AND t.status = 'completed'
              AND t.deleted_at IS NULL
            """,
            new { seasonId })).AsList();

        // 2. Clear existing standings and rebuild
        using var tx = conn.BeginTransaction();

        // Reset all points to 0
        await conn.ExecuteAsync(
            "UPDATE public.season_standings SET total_points = 0, standing_rank = NULL, version = version + 1, updated_at = NOW() WHERE season_id = @seasonId",
            new { seasonId }, tx);

        // 3. For each completed tournament, resolve placements and apply points
        foreach (var tournamentId in tournaments)
        {
            var placements = await ResolveTournamentPlacementsAsync(conn, tournamentId);
            if (placements.Count > 0)
            {
                await ApplyPointsToSeasonAsync(conn, seasonId, tournamentId, placements, ct, tx);
            }
        }

        // 4. Recalculate ranks
        await RecalculateRanksAsync(conn, seasonId, tx);

        tx.Commit();

        // Update sync timestamp
        await conn.ExecuteAsync(
            "UPDATE public.seasons SET last_standings_sync_at = NOW(), updated_at = NOW() WHERE id = @seasonId",
            new { seasonId });

        if (cache is not null)
        {
            try { await cache.RemoveAsync($"season:{seasonId}", ct); } catch { }
            try { await cache.RemoveAsync($"season-detail:{seasonId}", ct); } catch { }
        }

        logger.LogInformation("[SeasonSync] Full recalculation completed for season {SeasonId} across {Count} tournaments.",
            seasonId, tournaments.Count);
    }

    /// <summary>
    /// Resolves final placements for all teams in a completed tournament.
    /// Uses bracket elimination order for bracket tournaments, or match-based standings for group/swiss.
    /// </summary>
    private async Task<List<TeamPlacement>> ResolveTournamentPlacementsAsync(IDbConnection conn, Guid tournamentId)
    {
        var placements = new List<TeamPlacement>();

        // Strategy 1: Check if tournament has a winner_id set (bracket finals)
        var winnerId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT winner_id FROM public.tournaments WHERE id = @tournamentId",
            new { tournamentId });

        // Strategy 2: Get tournament stages and compute placements from bracket results
        var stages = (await conn.QueryAsync<dynamic>(
            """
            SELECT id, name, format, stage_order
            FROM public.tournament_stages
            WHERE tournament_id = @tournamentId
            ORDER BY stage_order DESC
            """,
            new { tournamentId })).AsList();

        if (stages.Count > 0)
        {
            // Use the last (highest order) stage for final placements
            var finalStageId = (Guid)stages[0].id;

            // Get all completed matches in the final stage, ordered by elimination round
            var matches = (await conn.QueryAsync<dynamic>(
                """
                SELECT m.id, m.team1_id, m.team2_id, m.winner_id, m.loser_id,
                       m.round_index, m.match_number, m.bracket_type,
                       m.team1_score, m.team2_score
                FROM public.brkt_matches m
                JOIN public.brkt_versions v ON v.id = m.version_id
                WHERE v.stage_id = @finalStageId
                  AND m.status = 'completed'
                ORDER BY m.round_index DESC, m.match_number ASC
                """,
                new { finalStageId })).AsList();

            if (matches.Count > 0)
            {
                placements = DeriveplacementsFromBracket(matches, winnerId);
            }
        }

        // Fallback: if no bracket stages, use tournament_participants with team-based ordering
        if (placements.Count == 0)
        {
            // Try using match win counts across all tournament matches
            var teamStats = (await conn.QueryAsync<dynamic>(
                """
                SELECT team_id, COUNT(*) AS matches_played,
                       SUM(CASE WHEN is_winner THEN 1 ELSE 0 END) AS wins,
                       SUM(CASE WHEN NOT is_winner THEN 1 ELSE 0 END) AS losses
                FROM (
                    SELECT m.team1_id AS team_id, (m.winner_id = m.team1_id) AS is_winner
                    FROM public.brkt_matches m
                    JOIN public.brkt_versions v ON v.id = m.version_id
                    JOIN public.tournament_stages ts ON ts.id = v.stage_id
                    WHERE ts.tournament_id = @tournamentId AND m.status = 'completed' AND m.team1_id IS NOT NULL
                    UNION ALL
                    SELECT m.team2_id AS team_id, (m.winner_id = m.team2_id) AS is_winner
                    FROM public.brkt_matches m
                    JOIN public.brkt_versions v ON v.id = m.version_id
                    JOIN public.tournament_stages ts ON ts.id = v.stage_id
                    WHERE ts.tournament_id = @tournamentId AND m.status = 'completed' AND m.team2_id IS NOT NULL
                ) sub
                GROUP BY team_id
                ORDER BY wins DESC, losses ASC
                """,
                new { tournamentId })).AsList();

            for (int i = 0; i < teamStats.Count; i++)
            {
                placements.Add(new TeamPlacement((Guid)teamStats[i].team_id, i + 1));
            }
        }

        // If we still have no placements, use registered participants (everyone gets last place)
        if (placements.Count == 0)
        {
            var participants = (await conn.QueryAsync<Guid>(
                """
                SELECT team_id FROM public.tournament_participants
                WHERE tournament_id = @tournamentId AND status NOT IN ('rejected', 'cancelled') AND team_id IS NOT NULL
                """,
                new { tournamentId })).AsList();

            for (int i = 0; i < participants.Count; i++)
            {
                placements.Add(new TeamPlacement(participants[i], i + 1));
            }
        }

        return placements;
    }

    /// <summary>
    /// Derives placement order from bracket matches.
    /// Winner of grand final = 1st, loser = 2nd, losers from semifinal round = 3rd-4th, etc.
    /// </summary>
    private static List<TeamPlacement> DeriveplacementsFromBracket(List<dynamic> matches, Guid? winnerId)
    {
        var placements = new List<TeamPlacement>();
        var placed = new HashSet<Guid>();
        int currentPlacement = 1;

        // Group matches by round (descending = final first)
        var roundGroups = matches
            .GroupBy(m => (int)m.round_index)
            .OrderByDescending(g => g.Key)
            .ToList();

        foreach (var roundGroup in roundGroups)
        {
            var losersInRound = new List<Guid>();

            foreach (var match in roundGroup)
            {
                Guid? winner = (Guid?)match.winner_id;
                Guid? loser = (Guid?)match.loser_id;

                // For the grand final, the winner gets 1st
                if (currentPlacement == 1 && winner.HasValue && !placed.Contains(winner.Value))
                {
                    placements.Add(new TeamPlacement(winner.Value, currentPlacement));
                    placed.Add(winner.Value);
                    currentPlacement++;
                }
                // Use explicit winnerId from tournament if available
                else if (currentPlacement == 1 && winnerId.HasValue && !placed.Contains(winnerId.Value))
                {
                    placements.Add(new TeamPlacement(winnerId.Value, currentPlacement));
                    placed.Add(winnerId.Value);
                    currentPlacement++;
                }

                if (loser.HasValue && !placed.Contains(loser.Value))
                {
                    losersInRound.Add(loser.Value);
                }
            }

            // All losers in this round share the same placement range
            foreach (var loser in losersInRound)
            {
                placements.Add(new TeamPlacement(loser, currentPlacement));
                placed.Add(loser);
            }
            if (losersInRound.Count > 0)
                currentPlacement += losersInRound.Count;
        }

        // If winnerId was set but wasn't in the matches, ensure 1st place
        if (winnerId.HasValue && !placed.Contains(winnerId.Value))
        {
            placements.Insert(0, new TeamPlacement(winnerId.Value, 1));
            // Shift others
            for (int i = 1; i < placements.Count; i++)
                placements[i] = placements[i] with { Placement = placements[i].Placement + 1 };
        }

        return placements;
    }

    /// <summary>
    /// Applies season point rules to the given placements and upserts season_standings.
    /// Returns the list of team_ids whose standings were actually modified.
    /// </summary>
    private async Task<List<Guid>> ApplyPointsToSeasonAsync(
        IDbConnection conn, Guid seasonId, Guid tournamentId,
        List<TeamPlacement> placements, CancellationToken ct, IDbTransaction? tx = null)
    {
        // Fetch point rules for this season that apply to this tournament (or season-wide rules)
        var pointRules = (await conn.QueryAsync<PointRuleRow>(
            """
            SELECT id, tournament_id, source_node_id, placement_start, placement_end, points,
                   qualification_status, destination_tournament_id, auto_create_qualification
            FROM public.season_point_rules
            WHERE season_id = @seasonId
              AND (tournament_id = @tournamentId OR tournament_id IS NULL)
            ORDER BY placement_start ASC
            """,
            new { seasonId, tournamentId }, tx)).AsList();

        if (pointRules.Count == 0)
        {
            logger.LogDebug("[SeasonSync] No point rules found for season {SeasonId} / tournament {TournamentId}.", seasonId, tournamentId);
            return [];
        }

        var affectedTeams = new List<Guid>();
        var upsertParams = new List<object>();

        // Map each team's placement to points
        foreach (var team in placements)
        {
            int totalPoints = 0;
            string? qualificationStatus = null;

            foreach (var rule in pointRules)
            {
                // Rule applies if tournament matches (or rule is season-wide) AND placement is in range
                if (rule.TournamentId.HasValue && rule.TournamentId.Value != tournamentId)
                    continue;

                if (team.Placement >= rule.PlacementStart && team.Placement <= rule.PlacementEnd)
                {
                    totalPoints += rule.Points;
                    if (!string.IsNullOrEmpty(rule.QualificationStatus))
                        qualificationStatus = rule.QualificationStatus;
                }
            }

            if (totalPoints == 0 && qualificationStatus is null)
                continue;

            affectedTeams.Add(team.TeamId);
            upsertParams.Add(new { seasonId, teamId = team.TeamId, points = totalPoints, qualStatus = qualificationStatus });
        }

        if (upsertParams.Count == 0)
            return [];

        // Batch upsert: single round-trip for all affected teams
        await conn.ExecuteAsync(
            """
            INSERT INTO public.season_standings (season_id, team_id, total_points, qualification_status)
            SELECT season_id, team_id, points, qual_status
            FROM UNNEST(@seasonIds::uuid[], @teamIds::uuid[], @pointsArr::int[], @qualStatuses::text[])
            AS t(season_id, team_id, points, qual_status)
            ON CONFLICT (season_id, team_id) DO UPDATE SET
                total_points = public.season_standings.total_points + excluded.total_points,
                qualification_status = COALESCE(excluded.qualification_status, public.season_standings.qualification_status),
                version = public.season_standings.version + 1,
                updated_at = NOW()
            """,
            new
            {
                seasonIds = upsertParams.Select(p => ((dynamic)p).seasonId).ToArray(),
                teamIds = upsertParams.Select(p => ((dynamic)p).teamId).ToArray(),
                pointsArr = upsertParams.Select(p => ((dynamic)p).points).ToArray(),
                qualStatuses = upsertParams.Select(p => ((dynamic)p).qualStatus).ToArray()
            }, tx);

        return affectedTeams;
    }

    private static async Task RecalculateRanksAsync(IDbConnection conn, Guid seasonId, IDbTransaction? tx = null)
    {
        await conn.ExecuteAsync(
            """
            WITH ranked AS (
                SELECT id, ROW_NUMBER() OVER (ORDER BY total_points DESC, updated_at ASC) AS new_rank
                FROM public.season_standings
                WHERE season_id = @seasonId
            )
            UPDATE public.season_standings ss
            SET standing_rank = ranked.new_rank
            FROM ranked
            WHERE ss.id = ranked.id
            """,
            new { seasonId }, tx);
    }

    // ── Internal DTOs ─────────────────────────────────────────────────────────────

    private sealed record LinkedSeasonRow(Guid SeasonId, string SeasonRole, string SeasonStatus);
    private sealed record TeamPlacement(Guid TeamId, int Placement);
    private sealed record PointRuleRow(
        Guid Id, Guid? TournamentId, Guid? SourceNodeId,
        int PlacementStart, int PlacementEnd, int Points,
        string? QualificationStatus, Guid? DestinationTournamentId, bool AutoCreateQualification);
}
