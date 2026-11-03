namespace Esportra.Api.Services.ScrimData;

public sealed record AgentMap(string Map, int Games, int Wins);

/// <summary>One agent across the team's scrim maps: how often it was played, how those maps went, and the figures from the maps that provide them.</summary>
public sealed record AgentSummary(
    string CharacterId, int Games, int Wins, double? WinRatePct, double? PickSharePct, double? Kd, int KdGames, double? Acs, int StatGames,
    IReadOnlyList<AgentMap> Maps);

/// <summary>One player on one agent.</summary>
public sealed record PlayerAgentSummary(
    Guid UserId, string? Name, string CharacterId, int Games, int Wins, double? WinRatePct, double? Kd, int KdGames, double? Acs, int StatGames);

/// <summary>
/// Agent and player-on-agent breakdowns from the same per-map player lines the rest of the data uses. A figure comes only from the maps that
/// provide it (screenshots carry kills, deaths and ACS but no first deaths) and always says how many maps that is. Results describe what
/// happened with an agent in play, not that the agent caused it.
/// </summary>
public static class ScrimAgentMath
{
    private static double? Ratio(double n, double d, int digits = 2) => d <= 0 ? null : Math.Round(n / d, digits);

    private static (double? Kd, int KdGames, double? Acs, int StatGames) Figures(IReadOnlyList<ScrimPlayerLine> lines)
    {
        var kd = lines.Where(l => l.Kills is not null && l.Deaths is not null).ToList();
        var acs = lines.Where(l => l.Acs is not null && l.RoundsPlayed is > 0).ToList();
        double rounds = acs.Sum(l => (double)l.RoundsPlayed!.Value);
        return (
            kd.Count == 0 ? null : Ratio(kd.Sum(l => (double)l.Kills!.Value), Math.Max(kd.Sum(l => (double)l.Deaths!.Value), 1)), kd.Count,
            rounds <= 0 ? null : Math.Round(acs.Sum(l => l.Acs!.Value * l.RoundsPlayed!.Value) / rounds, 1), acs.Count);
    }

    public static IReadOnlyList<AgentSummary> Agents(IReadOnlyList<ScrimGame> games)
    {
        var rows = games.SelectMany(g => g.Lines.Where(l => l.CharacterId is not null).Select(l => (Game: g, Line: l))).ToList();
        return rows.GroupBy(r => r.Line.CharacterId!.ToLowerInvariant()).Select(group =>
        {
            var wins = group.Count(r => r.Game.Result == "win");
            var (kd, kdGames, acs, statGames) = Figures(group.Select(r => r.Line).ToList());
            var maps = group.Where(r => r.Game.Map is not null).GroupBy(r => r.Game.Map!)
                .Select(m => new AgentMap(m.Key, m.Count(), m.Count(r => r.Game.Result == "win")))
                .OrderByDescending(m => m.Games).ThenBy(m => m.Map, StringComparer.Ordinal).ToList();
            return new AgentSummary(group.Key, group.Count(), wins, Ratio(100.0 * wins, group.Count(), 1), Ratio(100.0 * group.Count(), rows.Count, 1), kd, kdGames, acs, statGames, maps);
        }).OrderByDescending(a => a.Games).ThenBy(a => a.CharacterId, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<PlayerAgentSummary> PlayerAgents(IReadOnlyList<ScrimGame> games, IReadOnlyDictionary<Guid, (string? Username, string? Role)> people)
    {
        var rows = games.SelectMany(g => g.Lines.Where(l => l.UserId is not null && l.CharacterId is not null).Select(l => (Game: g, Line: l))).ToList();
        return rows.GroupBy(r => (User: r.Line.UserId!.Value, Agent: r.Line.CharacterId!.ToLowerInvariant())).Select(group =>
        {
            var wins = group.Count(r => r.Game.Result == "win");
            var (kd, kdGames, acs, statGames) = Figures(group.Select(r => r.Line).ToList());
            var name = people.TryGetValue(group.Key.User, out var p) && p.Username is not null ? p.Username : group.Select(r => r.Line.Name).FirstOrDefault(n => n is not null);
            return new PlayerAgentSummary(group.Key.User, name, group.Key.Agent, group.Count(), wins, Ratio(100.0 * wins, group.Count(), 1), kd, kdGames, acs, statGames);
        }).OrderBy(r => r.Name, StringComparer.Ordinal).ThenByDescending(r => r.Games).ThenBy(r => r.CharacterId, StringComparer.Ordinal).ToList();
    }
}
