using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Esportra.Api.Services.ProviderMatches;
using Esportra.Api.Services.ProviderMatches.Career;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Competitive intelligence, Phase 2: player and team career records built only from Riot-verified custom games.
/// Access is decided here (see <see cref="CareerQueries"/>); a viewer who may not see something gets no data, not a hidden field.
///
/// GET /api/competitive/players/{userId}/career?game=               — summary, agents, maps, teams, activity, first page of games
/// GET /api/competitive/players/{userId}/career/matches?before=&amp;limit= — older games
/// GET /api/competitive/teams/{teamId}/career                       — the same for a team
/// GET /api/competitive/teams/{teamId}/career/matches?before=&amp;limit=
/// </summary>
public static partial class CareerEndpoints
{
    private const int PageSize = 20;
    private const int MaxPage = 50;
    private static readonly string[] Basis = ["verified", "partially_verified"];
    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [GeneratedRegex("^[a-z0-9_-]{2,40}$")]
    private static partial Regex GameSlugPattern();

    public static void MapCareerEndpoints(this WebApplication app)
    {
        app.MapGet("/api/competitive/players/{userId:guid}/career", PlayerCareerAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/competitive/players/{userId:guid}/career/matches", PlayerMatchesAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/competitive/teams/{teamId:guid}/career", TeamCareerAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/competitive/teams/{teamId:guid}/career/matches", TeamMatchesAsync).RequireAuthorization("Authenticated");
    }

    private static IResult Json(object body) => Results.Text(JsonSerializer.Serialize(body, Snake), "application/json");

    private static string? CheckGame(string? game, out string slug)
    {
        slug = game ?? "valorant";
        if (!GameSlugPattern().IsMatch(slug)) return "invalid game";
        return slug == "valorant" ? null : "Career records are only available for VALORANT so far.";
    }

    private static int Page(int? limit) => Math.Clamp(limit ?? PageSize, 1, MaxPage);

    /// <summary>The cursor for the next page, only when rows beyond this page exist.</summary>
    private static (string? Cursor, bool More) Next(IReadOnlyList<DateTime?> shown, bool more) =>
        more && shown.Count > 0 && shown[^1] is { } last ? (last.ToUniversalTime().ToString("O"), true) : (null, false);

    private static object PlayerItem(PlayerMatchRow r) => new
    {
        started_at = r.StartedAt,
        context = ContextTypes.IsTournament(r.ContextType) ? "tournament" : "scrim",
        scrim_session_id = r.ScrimSessionId,
        tournament_match_id = r.TournamentMatchId,
        game_number = r.GameNumber,
        map = ProviderMatchVerifier.DisplayMap(r.MapId),
        character_id = r.CharacterId?.ToLowerInvariant(),
        team_name = r.TeamName,
        opponent_name = r.OpponentName,
        result = CareerMath.Result(r.RoundsWon, r.RoundsLost),
        rounds_won = r.RoundsWon,
        rounds_lost = r.RoundsLost,
        r.Kills,
        r.Deaths,
        r.Assists,
        acs = r.RoundsPlayed > 0 ? (double?)Math.Round((double)r.Score / r.RoundsPlayed, 1) : null,
        adr = r.RoundsPlayed > 0 ? (double?)Math.Round((double)r.DamageDealt / r.RoundsPlayed, 1) : null,
        headshot_share_pct = r.Headshots + r.Bodyshots + r.Legshots > 0
            ? (double?)Math.Round(100.0 * r.Headshots / (r.Headshots + r.Bodyshots + r.Legshots), 1) : null,
    };

    private static object TeamItem(TeamMatchRow r) => new
    {
        started_at = r.StartedAt,
        context = ContextTypes.IsTournament(r.ContextType) ? "tournament" : "scrim",
        scrim_session_id = r.ScrimSessionId,
        tournament_match_id = r.TournamentMatchId,
        game_number = r.GameNumber,
        map = ProviderMatchVerifier.DisplayMap(r.MapId),
        opponent_name = r.OpponentName,
        result = CareerMath.Result(r.RoundsWon, r.RoundsLost),
        rounds_won = r.RoundsWon,
        rounds_lost = r.RoundsLost,
    };

    private static async Task<IResult> PlayerCareerAsync(Guid userId, string? game, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext viewer) return Results.Unauthorized();
        if (CheckGame(game, out var slug) is { } bad) return Results.BadRequest(new { error = bad });
        using var conn = db.CreateConnection();

        var access = await CareerQueries.ResolvePlayerAccessAsync(conn, viewer, userId, slug);
        if (access.Level == "none") return Json(new { access = "none", game = slug });

        var rows = await CareerQueries.PlayerRowsAsync(conn, userId, access.SummaryTeamIds, true, [], null, CareerQueries.SummaryRowCap);
        var visible = rows.Where(r => access.FullHistory || ContextTypes.IsTournament(r.ContextType) || (r.TeamId is { } t && access.StaffTeamIds.Contains(t))).ToList();
        var history = visible.Take(PageSize).ToList();
        var (cursor, more) = Next(history.Select(h => h.StartedAt).ToList(), visible.Count > PageSize);

        return Json(new
        {
            access = access.Level,
            scope = access.Level switch { "team_staff" => "your_teams", "public" => "public", _ => "all" },
            game = slug,
            basis = Basis,
            has_data = rows.Count > 0,
            truncated = rows.Count >= CareerQueries.SummaryRowCap,
            sample = CareerMath.SampleOf(rows),
            totals = CareerMath.Totals(rows),
            agents = CareerMath.ByAgent(rows),
            maps = CareerMath.ByMap(rows),
            teams = CareerMath.ByTeam(rows),
            activity = CareerMath.Activity(rows),
            matches = history.Select(PlayerItem),
            next_before = cursor,
            has_more = more,
        });
    }

    private static async Task<IResult> PlayerMatchesAsync(
        Guid userId, string? game, DateTimeOffset? before, int? limit, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext viewer) return Results.Unauthorized();
        if (CheckGame(game, out var slug) is { } bad) return Results.BadRequest(new { error = bad });
        using var conn = db.CreateConnection();
        var access = await CareerQueries.ResolvePlayerAccessAsync(conn, viewer, userId, slug);
        if (access.Level == "none") return Json(new { access = "none", matches = Array.Empty<object>(), has_more = false });

        var page = Page(limit);
        var fetched = await CareerQueries.PlayerRowsAsync(conn, userId, access.SummaryTeamIds, access.FullHistory, access.StaffTeamIds, before?.UtcDateTime, page + 1);
        var rows = fetched.Take(page).ToList();
        var (cursor, more) = Next(rows.Select(r => r.StartedAt).ToList(), fetched.Count > page);
        return Json(new { access = access.Level, matches = rows.Select(PlayerItem), next_before = cursor, has_more = more });
    }

    private static async Task<IResult> TeamCareerAsync(Guid teamId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext viewer) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        if (!await TeamExistsAsync(conn, teamId)) return Results.NotFound();

        var access = await CareerQueries.ResolveTeamAccessAsync(conn, viewer, teamId);
        var rows = await CareerQueries.TeamRowsAsync(conn, teamId, access.IncludeScrims, null, CareerQueries.SummaryRowCap);
        var history = rows.Take(PageSize).ToList();
        var (cursor, more) = Next(history.Select(h => h.StartedAt).ToList(), rows.Count > PageSize);
        var players = access.Level == "member" ? await CareerQueries.TeamAppearancesAsync(conn, teamId) : [];

        return Json(new
        {
            access = access.Level,
            includes_scrims = access.IncludeScrims,
            game = "valorant",
            basis = Basis,
            has_data = rows.Count > 0,
            truncated = rows.Count >= CareerQueries.SummaryRowCap,
            sample = CareerMath.SampleOf(rows),
            totals = CareerMath.Totals(rows),
            form = CareerMath.Form(rows),
            maps = CareerMath.ByMap(rows),
            activity = CareerMath.Activity(rows),
            players = players.Select(p => new { user_id = p.UserId, username = p.Username, matches = p.Matches, last_played = p.LastPlayed }),
            matches = history.Select(TeamItem),
            next_before = cursor,
            has_more = more,
        });
    }

    private static async Task<IResult> TeamMatchesAsync(Guid teamId, DateTimeOffset? before, int? limit, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext viewer) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        if (!await TeamExistsAsync(conn, teamId)) return Results.NotFound();
        var access = await CareerQueries.ResolveTeamAccessAsync(conn, viewer, teamId);
        var page = Page(limit);
        var fetched = await CareerQueries.TeamRowsAsync(conn, teamId, access.IncludeScrims, before?.UtcDateTime, page + 1);
        var rows = fetched.Take(page).ToList();
        var (cursor, more) = Next(rows.Select(r => r.StartedAt).ToList(), fetched.Count > page);
        return Json(new { access = access.Level, matches = rows.Select(TeamItem), next_before = cursor, has_more = more });
    }

    private static async Task<bool> TeamExistsAsync(IDbConnection conn, Guid teamId) =>
        await Dapper.SqlMapper.ExecuteScalarAsync<bool>(conn, "SELECT EXISTS (SELECT 1 FROM public.teams WHERE id = @teamId)", new { teamId });
}
