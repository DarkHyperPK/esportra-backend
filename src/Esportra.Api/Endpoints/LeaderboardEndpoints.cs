using Dapper;
using Esportra.Contracts.Database;
using Esportra.Core.Tournaments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Public team leaderboard API, backed by the persisted leaderboard_team_stats
/// grain (team × game × region) rebuilt by LeaderboardStatsService.
///
/// Model A — game is the required base axis; region and country optionally
/// narrow the ranked pool. Rankings are computed per filtered pool with
/// deterministic tiebreakers: rp DESC → wins DESC → win_rate DESC →
/// tournaments_won DESC → name ASC. Mock/virtual teams never appear.
///
/// Freshness: event-driven (match finalized / winner set / placements resolved
/// enqueue the Hangfire rebuild) + hourly self-healing sweep + startup backfill.
/// </summary>
public static class LeaderboardEndpoints
{
    private const string CacheTag = "leaderboard";

    public static void MapLeaderboardEndpoints(this WebApplication app)
    {
        // GET /api/leaderboards/teams
        app.MapGet("/api/leaderboards/teams", async (
            IDbConnectionFactory db,
            HybridCache cache,
            [FromQuery] string? game = null,
            [FromQuery] string? country = null,
            [FromQuery] string? region = null,
            [FromQuery] int limit = 50,
            [FromQuery] int offset = 0,
            CancellationToken ct = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);

            // Model A: game is the base axis — a mixed-game "global" board is meaningless.
            if (string.IsNullOrWhiteSpace(game))
                return Results.BadRequest(new { error = "A 'game' parameter is required (optionally narrow with 'region' and/or 'country')." });

            // Canonicalize game through catalog aliases so /valorant and /Valo unify.
            using var conn0 = db.CreateConnection();
            var gameKey = await conn0.ExecuteScalarAsync<string>(
                new CommandDefinition(
                    """
                    SELECT COALESCE(
                        (SELECT a.game_slug FROM public.game_catalog_game_aliases a
                         WHERE LOWER(a.alias) = LOWER(btrim(@game))
                         ORDER BY a.version_id DESC LIMIT 1),
                        LOWER(btrim(@game)))
                    """,
                    new { game }, cancellationToken: ct));

            var normalizedCountry = EmptyToNull(country);
            var normalizedRegion = EmptyToNull(region);
            var cacheKey = $"leaderboard:teams:{gameKey}:{normalizedRegion}:{normalizedCountry}:{limit}:{offset}";

            var page = await cache.GetOrCreateAsync(
                cacheKey,
                async cancel =>
                {
                    using var conn = db.CreateConnection();
                    var items = (await conn.QueryAsync<LeaderboardRow>(
                        new CommandDefinition(TeamsQuery(),
                            new
                            {
                                Game = gameKey,
                                Country = normalizedCountry,
                                Region = normalizedRegion,
                                Limit = limit,
                                Offset = offset,
                            },
                            cancellationToken: cancel))).AsList();

                    var total = await conn.ExecuteScalarAsync<int>(
                        new CommandDefinition(TeamsCountQuery(),
                            new
                            {
                                Game = gameKey,
                                Country = normalizedCountry,
                                Region = normalizedRegion,
                            },
                            cancellationToken: cancel));

                    return new LeaderboardPage(total, items);
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(60) },
                tags: [CacheTag],
                cancellationToken: ct);

            return Results.Ok(new
            {
                total = page.Total,
                game = gameKey,
                country = normalizedCountry,
                region = normalizedRegion,
                limit,
                offset,
                items = page.Items.Select(r => new
                {
                    rank = r.Rank,
                    team_id = r.TeamId,
                    name = r.Name,
                    logo_url = r.LogoUrl,
                    country_code = r.CountryCode,
                    game = r.Game,
                    region = r.Region,
                    matches_played = r.MatchesPlayed,
                    wins = r.Wins,
                    losses = r.Losses,
                    win_rate = r.WinRate,
                    tournaments_played = r.TournamentsPlayed,
                    tournaments_won = r.TournamentsWon,
                    placement_points = r.PlacementPoints,
                    best_placement = r.BestPlacement,
                    rp = r.Rp,
                }),
            });
        });

        // GET /api/leaderboards/filters — values actually present in the stats table
        app.MapGet("/api/leaderboards/filters", async (
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var result = await cache.GetOrCreateAsync(
                "leaderboard:filters",
                async cancel =>
                {
                    using var conn = db.CreateConnection();
                    var games = (await conn.QueryAsync<string>(
                        new CommandDefinition(
                            """
                            SELECT DISTINCT game_key
                            FROM public.leaderboard_team_stats
                            ORDER BY game_key
                            """, cancellationToken: cancel))).AsList();

                    var regions = (await conn.QueryAsync<string>(
                        new CommandDefinition(
                            """
                            SELECT DISTINCT region_key
                            FROM public.leaderboard_team_stats
                            WHERE region_key <> 'global'
                            ORDER BY region_key
                            """, cancellationToken: cancel))).AsList();

                    var countries = (await conn.QueryAsync<string>(
                        new CommandDefinition(
                            """
                            SELECT DISTINCT t.country_code
                            FROM public.leaderboard_team_stats s
                            JOIN public.teams t ON t.id = s.team_id
                            WHERE t.country_code IS NOT NULL AND t.country_code <> ''
                            ORDER BY t.country_code
                            """, cancellationToken: cancel))).AsList();

                    return new LeaderboardFilters(games, regions, countries);
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(5) },
                tags: [CacheTag],
                cancellationToken: ct);

            return Results.Ok(new { games = result.Games, regions = result.Regions, countries = result.Countries });
        });

        // GET /api/leaderboards/health — ops diagnostics (admin): source-data counts
        // vs persisted grains, so an empty public board is instantly explainable.
        app.MapGet("/api/leaderboards/health", async (IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            var table = await conn.QuerySingleAsync<dynamic>(
                """
                SELECT COUNT(*)::int AS grain_rows,
                       MAX(updated_at) AS last_write
                FROM public.leaderboard_team_stats
                """);
            var sources = await conn.QuerySingleAsync<dynamic>(
                """
                SELECT
                    (SELECT COUNT(*) FROM public.brkt_matches
                     WHERE status = 'completed' AND winner_id IS NOT NULL)::int AS completed_matches_with_winner,
                    (SELECT COUNT(*) FROM public.tournaments WHERE status = 'completed')::int AS completed_tournaments,
                    (SELECT COUNT(*) FROM public.tournaments
                     WHERE status = 'completed' AND winner_id IS NOT NULL)::int AS crowned_winners,
                    (SELECT COUNT(*) FROM public.tournament_placements)::int AS placement_rows,
                    (SELECT COUNT(*) FROM public.tournament_placements tp
                     JOIN public.tournaments tr ON tr.id = tp.tournament_id
                     WHERE tr.status = 'completed')::int AS placements_in_completed_tournaments,
                    (SELECT COUNT(*) FROM public.tournament_placements tp
                     JOIN public.teams t ON t.id = tp.team_id
                     WHERE t.is_mock IS NOT TRUE AND (t.team_kind IS NULL OR t.team_kind <> 'mock'))::int AS placements_for_rankable_teams,
                    (SELECT COUNT(*) FROM public.tournament_placements tp
                     JOIN public.tournaments tr ON tr.id = tp.tournament_id
                     JOIN public.teams t ON t.id = tp.team_id
                     WHERE tr.status = 'completed'
                       AND t.is_mock IS NOT TRUE AND (t.team_kind IS NULL OR t.team_kind <> 'mock'))::int AS placements_eligible_all_filters
                """);
            return Results.Ok(new
            {
                table = new { rows = (int)table.grain_rows, last_write = (DateTime?)table.last_write },
                sources,
            });
        });

        // GET /api/leaderboards/meta — live formula description for UI explainers
        app.MapGet("/api/leaderboards/meta", () => Results.Ok(new
        {
            win_points = LeaderboardFormula.WinPoints,
            loss_points = LeaderboardFormula.LossPoints,
            tournament_win_points = LeaderboardFormula.TournamentWinPoints,
            placement_points = new object[]
            {
                new { placement = 1, points = LeaderboardFormula.Placement1st },
                new { placement = 2, points = LeaderboardFormula.Placement2nd },
                new { placement = 3, points = LeaderboardFormula.Placement3rd },
                new { placement = 4, points = LeaderboardFormula.Placement4th },
                new { from_placement = 5, to_placement = 8, points = LeaderboardFormula.Placement5thTo8th },
                new { from_placement = 9, to_placement = 16, points = LeaderboardFormula.Placement9thTo16th },
            },
        }));
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Model A: game is always required; region/country optionally narrow the pool.
    /// Stats are per-grain rows (team × game × region) — no aggregation — and rank
    /// is assigned after filtering so each filtered pool ranks within itself.
    /// </summary>
    private static string TeamsQuery() =>
        """
        WITH pool AS (
            SELECT
                s.team_id, s.game_key, s.region_key,
                s.matches_played, s.wins, s.losses,
                s.tournaments_played, s.tournaments_won,
                s.placement_points, s.best_placement, s.rp,
                t.name, t.logo_url, t.country_code
            FROM public.leaderboard_team_stats s
            JOIN public.teams t ON t.id = s.team_id
            WHERE s.game_key = @Game
              AND (@Region::text IS NULL OR s.region_key = @Region)
              AND (@Country::text IS NULL OR t.country_code = @Country)
              AND t.is_mock IS NOT TRUE
              AND (t.team_kind IS NULL OR t.team_kind <> 'mock')
              AND (s.matches_played > 0 OR s.rp > 0)
        ),
        ranked AS (
            SELECT
                pool.*,
                ROW_NUMBER() OVER (
                    ORDER BY pool.rp DESC,
                             pool.wins DESC,
                             COALESCE(pool.wins * 100.0 / NULLIF(pool.matches_played, 0), 0) DESC,
                             pool.tournaments_won DESC,
                             pool.name ASC
                ) AS rank
            FROM pool
        )
        SELECT rank, team_id, game_key AS game, region_key AS region,
               name, logo_url, country_code,
               matches_played, wins, losses,
               ROUND(COALESCE(wins * 100.0 / NULLIF(matches_played, 0), 0), 1) AS win_rate,
               tournaments_played, tournaments_won, placement_points, best_placement, rp
        FROM ranked
        ORDER BY rank
        LIMIT @Limit OFFSET @Offset
        """;

    private static string TeamsCountQuery() =>
        """
        SELECT COUNT(*)::int
        FROM public.leaderboard_team_stats s
        JOIN public.teams t ON t.id = s.team_id
        WHERE s.game_key = @Game
          AND (@Region::text IS NULL OR s.region_key = @Region)
          AND (@Country::text IS NULL OR t.country_code = @Country)
          AND t.is_mock IS NOT TRUE
          AND (t.team_kind IS NULL OR t.team_kind <> 'mock')
          AND (s.matches_played > 0 OR s.rp > 0)
        """;

    private sealed record LeaderboardPage(int Total, IReadOnlyList<LeaderboardRow> Items);

    private sealed record LeaderboardFilters(IReadOnlyList<string> Games, IReadOnlyList<string> Regions, IReadOnlyList<string> Countries);

    private sealed record LeaderboardRow(
        long Rank,
        Guid TeamId,
        string Game,
        string Region,
        string Name,
        string? LogoUrl,
        string? CountryCode,
        int MatchesPlayed,
        int Wins,
        int Losses,
        decimal WinRate,
        int TournamentsPlayed,
        int TournamentsWon,
        int PlacementPoints,
        int? BestPlacement,
        long Rp);
}
