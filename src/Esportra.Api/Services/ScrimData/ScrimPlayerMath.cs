namespace Esportra.Api.Services.ScrimData;

public sealed record AgentUse(string CharacterId, int Games);

/// <summary>
/// One roster player across the team's scrim maps. Each figure uses only the maps that provide it, and says how many:
/// first deaths exist only for full-data maps, so a player with screenshot maps shows "from N of M".
/// </summary>
public sealed record ScrimPlayerSummary(
    Guid UserId, string? Name, int Games, int Wins, int FullGames, int ScreenshotGames,
    double? WinRatePct, double? Kd, double? Kda, int KdaGames, double? Acs, double? Adr, int StatGames, double? HsPct,
    int FirstKills, int? FirstDeaths, int FirstDeathGames, int Plants, int Defuses, IReadOnlyList<AgentUse> Agents);

public static class ScrimPlayerMath
{
    private static double? Ratio(double n, double d, int digits = 2) => d <= 0 ? null : Math.Round(n / d, digits);

    public static IReadOnlyList<ScrimPlayerSummary> Players(
        IReadOnlyList<ScrimGame> games, IReadOnlyDictionary<Guid, (string? Username, string? Role)> people)
    {
        var lines = games.SelectMany(g => g.Lines.Where(l => l.UserId is not null).Select(l => (Game: g, Line: l))).ToList();
        return lines.GroupBy(x => x.Line.UserId!.Value)
            .Select(group => Summarize(group.Key, group.ToList(), people))
            .OrderByDescending(p => p.Games).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    private static ScrimPlayerSummary Summarize(
        Guid userId, List<(ScrimGame Game, ScrimPlayerLine Line)> items, IReadOnlyDictionary<Guid, (string? Username, string? Role)> people)
    {
        var wins = items.Count(i => i.Game.Result == "win");
        var kda = items.Where(i => i.Line.Kills is not null && i.Line.Deaths is not null && i.Line.Assists is not null).ToList();
        var acs = items.Where(i => i.Line.Acs is not null && i.Line.RoundsPlayed is > 0).ToList();
        var adr = items.Where(i => i.Line.Adr is not null && i.Line.RoundsPlayed is > 0).ToList();
        var hs = items.Where(i => i.Line.HsPct is not null && i.Line.RoundsPlayed is > 0).ToList();
        var fd = items.Where(i => i.Line.FirstDeaths is not null).ToList();
        double kills = kda.Sum(i => i.Line.Kills!.Value), deaths = kda.Sum(i => i.Line.Deaths!.Value), assists = kda.Sum(i => i.Line.Assists!.Value);
        var agents = items.Where(i => i.Line.CharacterId is not null).GroupBy(i => i.Line.CharacterId!)
            .Select(g => new AgentUse(g.Key, g.Count())).OrderByDescending(a => a.Games).ThenBy(a => a.CharacterId, StringComparer.Ordinal).ToList();
        var name = people.TryGetValue(userId, out var p) && p.Username is not null ? p.Username : items.Select(i => i.Line.Name).FirstOrDefault(n => n is not null);

        return new ScrimPlayerSummary(
            userId, name, items.Count, wins, items.Count(i => i.Line.Tier == DataTier.Full), items.Count(i => i.Line.Tier == DataTier.Screenshot),
            Ratio(100.0 * wins, items.Count, 1), kda.Count == 0 ? null : Ratio(kills, Math.Max(deaths, 1)),
            kda.Count == 0 ? null : Ratio(kills + assists, Math.Max(deaths, 1)), kda.Count,
            Weighted(acs, i => i.Line.Acs!.Value), Weighted(adr, i => i.Line.Adr!.Value), acs.Count, Weighted(hs, i => i.Line.HsPct!.Value, 1),
            items.Sum(i => i.Line.FirstKills ?? 0), fd.Count == 0 ? null : fd.Sum(i => i.Line.FirstDeaths!.Value), fd.Count,
            items.Sum(i => i.Line.Plants ?? 0), items.Sum(i => i.Line.Defuses ?? 0), agents);
    }

    /// <summary>Round-weighted mean: a long map counts for more than a short one, as in the career formulas.</summary>
    private static double? Weighted(List<(ScrimGame Game, ScrimPlayerLine Line)> items, Func<(ScrimGame Game, ScrimPlayerLine Line), double> value, int digits = 1)
    {
        var rounds = items.Sum(i => (double)i.Line.RoundsPlayed!.Value);
        return rounds <= 0 ? null : Math.Round(items.Sum(i => value(i) * i.Line.RoundsPlayed!.Value) / rounds, digits);
    }
}
