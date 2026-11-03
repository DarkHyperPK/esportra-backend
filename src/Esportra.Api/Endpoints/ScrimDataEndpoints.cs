using System.Text.Json;
using Esportra.Api.Services.ProviderMatches.Career;
using Esportra.Api.Services.ScrimData;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

/// <summary>
/// "Your scrims data": a team's own scrim record, map pool, compositions, players and lineups, for its members.
/// Built only from scrim maps both teams confirmed, from the best data each map has: an auto-fetched Riot game
/// (full detail), a screenshot (limited: what the scoreboard shows) or just the score. Nothing here is public.
///
/// GET /api/scrims/teams/{teamId}/data
/// </summary>
public static class ScrimDataEndpoints
{
    private const int RecentGames = 100;
    private const int TopCompositions = 12;
    private const int TopLineups = 10;
    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static void MapScrimDataEndpoints(this WebApplication app) =>
        app.MapGet("/api/scrims/teams/{teamId:guid}/data", TeamDataAsync).RequireAuthorization("Authenticated");

    private static async Task<IResult> TeamDataAsync(Guid teamId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext viewer) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        if (!await ScrimDataQueries.TeamExistsAsync(conn, teamId)) return Results.NotFound();
        if (!await ScrimDataQueries.CanViewAsync(conn, viewer, teamId)) return Results.Forbid();

        var rows = await ScrimDataQueries.MapRowsAsync(conn, teamId, ScrimDataQueries.MapCap);
        var riotIds = rows.Where(r => r.AssociationStatus is "verified" or "partially_verified").Select(r => r.GameId).ToArray();
        var riotGames = await ScrimDataQueries.RiotGamesAsync(conn, teamId, riotIds);
        var riotPlayers = await ScrimDataQueries.RiotPlayersAsync(conn, teamId, riotGames.Keys.ToArray());
        var (awaiting, disputed) = await ScrimDataQueries.UncountedAsync(conn, teamId);

        var set = ScrimDataBuilder.Build(rows, riotGames, riotPlayers, awaiting, disputed);
        var people = await ScrimDataQueries.PeopleAsync(conn, teamId, set.Games.SelectMany(g => g.Lines).Where(l => l.UserId is not null).Select(l => l.UserId!.Value).Distinct().ToArray());
        var details = set.Games.Select(g => g.Detail).ToList();
        var roster = RosterPlayers(set.Games, people);
        var maps = IntelligenceMath.Maps(details);
        var lineups = IntelligenceMath.Lineups(details, roster);

        return Results.Text(JsonSerializer.Serialize(new
        {
            team_id = teamId,
            game = "valorant",
            coverage = set.Coverage,
            has_data = set.Games.Count > 0,
            truncated = rows.Count >= ScrimDataQueries.MapCap,
            min_map_sample = IntelligenceMath.MinMapSample,
            sample = new { maps = details.Count, from = details.Min(d => d.StartedAt), to = details.Max(d => d.StartedAt) },
            record = IntelligenceMath.Record(details),
            form = IntelligenceMath.Form(details),
            activity = IntelligenceMath.Activity(details),
            maps,
            unplayed_maps = IntelligenceMath.Unplayed(await ScrimDataQueries.PoolMapsAsync(conn), maps),
            compositions = IntelligenceMath.Compositions(details).Take(TopCompositions),
            players = ScrimPlayerMath.Players(set.Games, people),
            agents = ScrimAgentMath.Agents(set.Games),
            player_agents = ScrimAgentMath.PlayerAgents(set.Games, people),
            roster = new
            {
                lineups = lineups.Take(TopLineups),
                stability = IntelligenceMath.Stability(lineups, roster),
                substitutes = IntelligenceMath.Substitutes(details, roster),
                pairs = IntelligenceMath.Pairs(details, roster),
            },
            recent = set.Games.Take(RecentGames).Select(g => new
            {
                session_id = g.Row.SessionId,
                game_number = g.Row.GameNumber,
                played_at = g.Row.PlayedAt,
                map = g.Map,
                opponent = g.Row.OpponentName,
                result = g.Result,
                rounds_won = g.Detail.RoundsWon,
                rounds_lost = g.Detail.RoundsLost,
                tier = g.Tier,
            }),
        }, Snake), "application/json");
    }

    /// <summary>Who played each map: frozen roster for Riot games, the matched roster user for screenshots (current role: it is not stored per map).</summary>
    private static List<GamePlayer> RosterPlayers(IReadOnlyList<ScrimGame> games, IReadOnlyDictionary<Guid, (string? Username, string? Role)> people) =>
        games.SelectMany(g => g.Lines.Where(l => l.UserId is not null).Select(l =>
        {
            var person = people.GetValueOrDefault(l.UserId!.Value);
            return new GamePlayer(g.Row.GameId, l.UserId!.Value, person.Username ?? l.Name, l.Tier == DataTier.Full ? l.MemberRole : person.Role);
        })).ToList();
}
