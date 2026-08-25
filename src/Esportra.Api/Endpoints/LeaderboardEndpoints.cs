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
/// Scopes:
///   global  — all regions aggregated per team (optional country filter narrows the pool)
///   region  — rankings within one tournaments.region value
///   country — rankings within one teams.country_code
///
/// Ranks are deterministic: rp DESC → wins DESC → win_rate DESC →
/// tournaments_won DESC → name ASC. Mock/virtual teams never appear.
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
            [FromQuery] string? scope = null,
            [FromQuery] string? game = null,
            [FromQuery] string? country = null,
            [FromQuery] string? region = null,
            [FromQuery] int limit = 50,
            [FromQuery] int offset = 0,
            CancellationToken ct = default) =>
        {
            var normalizedScope = NormalizeScope(scope);
            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);

            if (normalizedScope == "region" && string.IsNullOrWhiteSpace(region))
                return Results.BadRequest(new { error = "scope=region requires a 'region' parameter." });
            if (normalizedScope == "country" && string.IsNullOrWhiteSpace(country))
                return Results.BadRequest(new { error = "scope=country requires a 'country' parameter." });

            // Canonicalize game through catalog aliases so /valorant and /Valo unify.
            string? gameKey = null;
            if (!string.IsNullOrWhiteSpace(game))
            {
                using var conn0 = db.CreateConnection();
                gameKey = await conn0.ExecuteScalarAsync<string?>(
                    new CommandDefinition(
                        """
                        SELECT COALESCE(
                            (SELECT a.game_slug FROM public.game_catalog_game_aliases a
                             WHERE LOWER(a.alias) = LOWER(btrim(@game))
                             ORDER BY a.version_id DESC LIMIT 1),
                            LOWER(btrim(@game)))
                        """,
                        new { game }, cancellationToken: ct));
            }

            bool aggregateRegions = normalizedScope != "region";
            var cacheKey = $"leaderboard:teams:{normalizedScope}:{gameKey}:{country}:{(aggregateRegions ? null : region)}:{limit}:{offset}";

            var page = await cache.GetOrCreateAsync(
                cacheKey,
                async cancel =>
                {
                    using var conn = db.CreateConnection();
                    var items = (await conn.QueryAsync<LeaderboardRow>(
                        new CommandDefinition(TeamsQuery(aggregateRegions),
                            new
                            {
                                Game = gameKey,
                                Country = EmptyToNull(country),
                                Region = aggregateRegions ? null : EmptyToNull(region),
                                Limit = limit,
                                Offset = offset,
                            },
                            cancellationToken: cancel))).AsList();

                    var total = await conn.ExecuteScalarAsync<int>(
                        new CommandDefinition(TeamsCountQuery(aggregateRegions),
                            new
                            {
                                Game = gameKey,
                                Country = EmptyToNull(country),
                                Region = aggregateRegions ? null : EmptyToNull(region),
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
                scope = normalizedScope,
                game = gameKey,
                country = EmptyToNull(country),
                region = aggregateRegions ? null : EmptyToNull(region),
                limit,
                offset,
                items = page.Items.Select(r => new
                {
                    rank = r.Rank,
                    team_id = r.TeamId,
                    name = r.Name,
                    logo_url = r.LogoUrl,
                    country_code = r.CountryCode,
                    regions = string.IsNullOrEmpty(r.RegionsCsv)
                        ? Array.Empty<string>()
                        : r.RegionsCsv.Split(','),
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

    private static string NormalizeScope(string? scope) => scope?.Trim().ToLowerInvariant() switch
    {
        "region" => "region",
        "country" => "country",
        _ => "global",
    };

    /// <summary>
    /// Shared ranked-page query. When aggregating (global/country scopes), stats are
    /// summed across region rows; region scope reads the single matching row set.
    /// Rank is assigned after filtering so country pools rank within themselves.
    /// </summary>
    private static string TeamsQuery(bool aggregateRegions)
    {
        var regionFilter = aggregateRegions
            ? ""
            : "AND s.region_key = @Region";
        var groupBy = aggregateRegions
            ? "GROUP BY s.team_id"
            : "GROUP BY s.team_id, s.region_key";

        return $"""
            WITH agg AS (
                SELECT
                    s.team_id,
                    SUM(s.matches_played)::int      AS matches_played,
                    SUM(s.wins)::int                AS wins,
                    SUM(s.losses)::int              AS losses,
                    SUM(s.tournaments_played)::int  AS tournaments_played,
                    SUM(s.tournaments_won)::int     AS tournaments_won,
                    SUM(s.placement_points)::int    AS placement_points,
                    SUM(s.rp)::bigint               AS rp,
                    MIN(s.best_placement)           AS best_placement,
                    COALESCE(ARRAY_TO_STRING(ARRAY_REMOVE(ARRAY_AGG(DISTINCT s.region_key), 'global'), ','), '') AS regions_csv
                FROM public.leaderboard_team_stats s
                WHERE (@Game::text IS NULL OR s.game_key = @Game)
                {regionFilter}
                {groupBy}
            ),
            filtered AS (
                SELECT agg.*, t.name, t.logo_url, t.country_code
                FROM agg
                JOIN public.teams t ON t.id = agg.team_id
                WHERE t.is_mock IS NOT TRUE
                  AND (t.team_kind IS NULL OR t.team_kind <> 'mock')
                  AND (@Country::text IS NULL OR t.country_code = @Country)
                  AND (agg.matches_played > 0 OR agg.rp > 0)
            ),
            ranked AS (
                SELECT
                    filtered.*,
                    ROW_NUMBER() OVER (
                        ORDER BY filtered.rp DESC,
                                 filtered.wins DESC,
                                 COALESCE(filtered.wins * 100.0 / NULLIF(filtered.matches_played, 0), 0) DESC,
                                 filtered.tournaments_won DESC,
                                 filtered.name ASC
                    ) AS rank
                FROM filtered
            )
            SELECT rank, team_id, name, logo_url, country_code, regions_csv,
                   matches_played, wins, losses,
                   ROUND(COALESCE(wins * 100.0 / NULLIF(matches_played, 0), 0), 1) AS win_rate,
                   tournaments_played, tournaments_won, placement_points, best_placement, rp
            FROM ranked
            ORDER BY rank
            LIMIT @Limit OFFSET @Offset
            """;
    }

    private static string TeamsCountQuery(bool aggregateRegions)
    {
        var regionFilter = aggregateRegions
            ? ""
            : "AND s.region_key = @Region";
        var groupBy = aggregateRegions
            ? "GROUP BY s.team_id"
            : "GROUP BY s.team_id, s.region_key";

        return $"""
            WITH agg AS (
                SELECT
                    s.team_id,
                    SUM(s.matches_played)::int AS matches_played,
                    SUM(s.rp)::bigint          AS rp
                FROM public.leaderboard_team_stats s
                WHERE (@Game::text IS NULL OR s.game_key = @Game)
                {regionFilter}
                {groupBy}
            )
            SELECT COUNT(*)::int
            FROM agg
            JOIN public.teams t ON t.id = agg.team_id
            WHERE t.is_mock IS NOT TRUE
              AND (t.team_kind IS NULL OR t.team_kind <> 'mock')
              AND (@Country::text IS NULL OR t.country_code = @Country)
              AND (agg.matches_played > 0 OR agg.rp > 0)
            """;
    }

    private sealed record LeaderboardPage(int Total, IReadOnlyList<LeaderboardRow> Items);

    private sealed record LeaderboardFilters(IReadOnlyList<string> Games, IReadOnlyList<string> Regions, IReadOnlyList<string> Countries);

    private sealed record LeaderboardRow(
        long Rank,
        Guid TeamId,
        string Name,
        string? LogoUrl,
        string? CountryCode,
        string RegionsCsv,
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
