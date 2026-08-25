using System.Data;
using Dapper;
using Esportra.Contracts.Database;
using Microsoft.Extensions.Logging;

namespace Esportra.Core.Tournaments;

/// <summary>
/// Central RP formula for the team leaderboard. Single source of truth —
/// the API /meta endpoint exposes these values and the recompute SQL binds them
/// as parameters, so changing weights here updates every consumer.
///
/// RP = wins × WinPoints − losses × LossPoints
///      + Σ placement points earned in completed tournaments
///      + TournamentWinPoints for each title NOT already covered by resolved
///        placements (admin-crowned winners without bracket data).
/// A champion's own tournament always nets at least TournamentWinPoints total
/// (GREATEST floor against their 1st-place placement value) — never double-counted.
/// </summary>
public static class LeaderboardFormula
{
    public const int WinPoints = 50;
    public const int LossPoints = 10;
    public const int TournamentWinPoints = 500;

    public const int Placement1st = 500;
    public const int Placement2nd = 300;
    public const int Placement3rd = 200;
    public const int Placement4th = 100;
    public const int Placement5thTo8th = 50;
    public const int Placement9thTo16th = 25;

    /// <summary>Placement → points mapping mirrored by the recompute SQL.</summary>
    public static int PlacementPoints(int placement) => placement switch
    {
        1 => Placement1st,
        2 => Placement2nd,
        3 => Placement3rd,
        4 => Placement4th,
        >= 5 and <= 8 => Placement5thTo8th,
        >= 9 and <= 16 => Placement9thTo16th,
        _ => 0,
    };
}

/// <summary>
/// Rebuilds <c>public.leaderboard_team_stats</c> from authoritative sources
/// (completed matches, resolved placements, awarded titles). Full transactional
/// DELETE+INSERT recompute — the table is small and read traffic is served from
/// HybridCache, so unconditional rebuilds on a short interval are cheap and
/// eliminate drift/incremental-update bugs entirely.
/// </summary>
public sealed class LeaderboardStatsService(IDbConnectionFactory db, ILogger<LeaderboardStatsService> logger)
{
    private const string GameKeyExpr =
        "COALESCE(ga.game_slug, LOWER(btrim(tr.game)))";

    private const string RegionKeyExpr =
        "COALESCE(NULLIF(LOWER(btrim(tr.region)), ''), 'global')";

    // Applies to every team join: mock/virtual filler teams never rank.
    private const string RealTeamFilter =
        "AND t.is_mock IS NOT TRUE AND (t.team_kind IS NULL OR t.team_kind <> 'mock')";

    private const string AliasLookup =
        """
        LEFT JOIN LATERAL (
            SELECT a.game_slug
            FROM public.game_catalog_game_aliases a
            WHERE LOWER(a.alias) = LOWER(btrim(tr.game))
            ORDER BY a.version_id DESC
            LIMIT 1
        ) ga ON TRUE
        """;

    private const string PlacementPointsSql = """
        CASE
            WHEN tp.placement = 1 THEN @Placement1st
            WHEN tp.placement = 2 THEN @Placement2nd
            WHEN tp.placement = 3 THEN @Placement3rd
            WHEN tp.placement = 4 THEN @Placement4th
            WHEN tp.placement BETWEEN 5 AND 8 THEN @Placement5thTo8th
            WHEN tp.placement BETWEEN 9 AND 16 THEN @Placement9thTo16th
            ELSE 0
        END
        """;

    /// <summary>
    /// Full rebuild of leaderboard_team_stats. Returns the number of grain rows written.
    /// </summary>
    public async Task<int> RecomputeAsync(CancellationToken ct = default)
    {
        var insertSql = $"""
            INSERT INTO public.leaderboard_team_stats
                (team_id, game_key, region_key, matches_played, wins, losses,
                 tournaments_played, tournaments_won, placement_points, best_placement, rp)
            WITH match_stats AS (
                SELECT
                    t.id                                   AS team_id,
                    {GameKeyExpr}                          AS game_key,
                    {RegionKeyExpr}                        AS region_key,
                    COUNT(*)::int                          AS matches_played,
                    COUNT(*) FILTER (WHERE m.winner_id = t.id)::int AS wins,
                    COUNT(*) FILTER (WHERE m.winner_id IS NOT NULL AND m.winner_id <> t.id)::int AS losses
                FROM public.brkt_matches m
                JOIN public.brkt_versions v  ON v.id = m.version_id
                JOIN public.tournaments tr   ON tr.id = v.tournament_id
                    AND tr.status = 'completed' AND tr.deleted_at IS NULL
                JOIN public.teams t          ON t.id IN (m.team1_id, m.team2_id)
                    {RealTeamFilter}
                {AliasLookup}
                WHERE m.status = 'completed' AND m.winner_id IS NOT NULL
                GROUP BY t.id, 2, 3
            ),
            placement_stats AS (
                SELECT
                    tp.team_id,
                    {GameKeyExpr}                          AS game_key,
                    {RegionKeyExpr}                        AS region_key,
                    SUM(
                        CASE
                            WHEN tr.winner_id = tp.team_id
                                THEN GREATEST(@TournamentWinPoints, {PlacementPointsSql})
                            ELSE {PlacementPointsSql}
                        END
                    )::int                                 AS placement_points,
                    MIN(tp.placement)::int                 AS best_placement
                FROM public.tournament_placements tp
                JOIN public.tournaments tr ON tr.id = tp.tournament_id
                    AND tr.status = 'completed' AND tr.deleted_at IS NULL
                JOIN public.teams t        ON t.id = tp.team_id
                    {RealTeamFilter}
                {AliasLookup}
                GROUP BY tp.team_id, 2, 3
            ),
            title_stats AS (
                SELECT
                    w.id                                   AS team_id,
                    {GameKeyExpr}                          AS game_key,
                    {RegionKeyExpr}                        AS region_key,
                    COUNT(DISTINCT tr.id)::int             AS titles,
                    COUNT(DISTINCT tr.id) FILTER (WHERE NOT EXISTS (
                        SELECT 1 FROM public.tournament_placements x
                        WHERE x.tournament_id = tr.id AND x.team_id = w.id
                    ))::int                                AS uncovered_titles
                FROM public.tournaments tr
                JOIN public.teams w ON w.id = tr.winner_id
                    {RealTeamFilter}
                {AliasLookup}
                WHERE tr.status = 'completed' AND tr.deleted_at IS NULL AND tr.winner_id IS NOT NULL
                GROUP BY w.id, 2, 3
            ),
            participation AS (
                SELECT team_id, tournament_id, game_key, region_key FROM (
                    SELECT DISTINCT t.id AS team_id, tr.id AS tournament_id,
                           {GameKeyExpr} AS game_key, {RegionKeyExpr} AS region_key
                    FROM public.brkt_matches m
                    JOIN public.brkt_versions v ON v.id = m.version_id
                    JOIN public.tournaments tr ON tr.id = v.tournament_id
                        AND tr.status = 'completed' AND tr.deleted_at IS NULL
                    JOIN public.teams t ON t.id IN (m.team1_id, m.team2_id)
                        {RealTeamFilter}
                    {AliasLookup}
                    WHERE m.status = 'completed'
                    UNION
                    SELECT DISTINCT tp.team_id, tr.id,
                           {GameKeyExpr}, {RegionKeyExpr}
                    FROM public.tournament_placements tp
                    JOIN public.tournaments tr ON tr.id = tp.tournament_id
                        AND tr.status = 'completed' AND tr.deleted_at IS NULL
                    JOIN public.teams t ON t.id = tp.team_id
                        {RealTeamFilter}
                    {AliasLookup}
                ) p
            ),
            played AS (
                SELECT team_id, game_key, region_key, COUNT(*)::int AS tournaments_played
                FROM participation
                GROUP BY team_id, game_key, region_key
            )
            SELECT
                COALESCE(ms.team_id, ps.team_id, ts.team_id, pl.team_id),
                COALESCE(ms.game_key, ps.game_key, ts.game_key, pl.game_key),
                COALESCE(ms.region_key, ps.region_key, ts.region_key, pl.region_key),
                COALESCE(ms.matches_played, 0),
                COALESCE(ms.wins, 0),
                COALESCE(ms.losses, 0),
                COALESCE(pl.tournaments_played, 0),
                COALESCE(ts.titles, 0),
                COALESCE(ps.placement_points, 0),
                ps.best_placement,
                (COALESCE(ms.wins, 0) * @WinPoints)
                - (COALESCE(ms.losses, 0) * @LossPoints)
                + COALESCE(ps.placement_points, 0)
                + (COALESCE(ts.uncovered_titles, 0) * @TournamentWinPoints)
            FROM match_stats ms
            FULL JOIN placement_stats ps USING (team_id, game_key, region_key)
            FULL JOIN title_stats    ts USING (team_id, game_key, region_key)
            FULL JOIN played         pl USING (team_id, game_key, region_key);
            """;

        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM public.leaderboard_team_stats;",
            cancellationToken: ct));

        var written = await conn.ExecuteAsync(new CommandDefinition(insertSql, new
        {
            WinPoints = LeaderboardFormula.WinPoints,
            LossPoints = LeaderboardFormula.LossPoints,
            TournamentWinPoints = LeaderboardFormula.TournamentWinPoints,
            Placement1st = LeaderboardFormula.Placement1st,
            Placement2nd = LeaderboardFormula.Placement2nd,
            Placement3rd = LeaderboardFormula.Placement3rd,
            Placement4th = LeaderboardFormula.Placement4th,
            Placement5thTo8th = LeaderboardFormula.Placement5thTo8th,
            Placement9thTo16th = LeaderboardFormula.Placement9thTo16th,
        }, tx, cancellationToken: ct));

        tx.Commit();

        logger.LogInformation(
            "[Leaderboard] Stats recomputed: {GrainRows} team×game×region row(s) written.", written);
        return written;
    }

    public sealed record TeamLeaderboardSummary(
        int Wins,
        int Losses,
        int MatchesPlayed,
        double WinRate,
        int TournamentsWon,
        long Rp);

    /// <summary>
    /// Aggregated all-grain summary for a single team (admin drawer, future profile cards).
    /// Shape-compatible with the previous inline admin SQL.
    /// </summary>
    public async Task<TeamLeaderboardSummary?> GetTeamSummaryAsync(Guid teamId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<(int wins, int losses, int played, int titles, long rp)>(
            new CommandDefinition(
                """
                SELECT COALESCE(SUM(wins), 0)::int,
                       COALESCE(SUM(losses), 0)::int,
                       COALESCE(SUM(matches_played), 0)::int,
                       COALESCE(SUM(tournaments_won), 0)::int,
                       COALESCE(SUM(rp), 0)::bigint
                FROM public.leaderboard_team_stats
                WHERE team_id = @teamId
                """,
                new { teamId }, cancellationToken: ct));

        return new TeamLeaderboardSummary(
            row.wins,
            row.losses,
            row.played,
            row.played > 0 ? Math.Round(row.wins * 100.0 / row.played, 1) : 0,
            row.titles,
            row.rp);
    }
}
