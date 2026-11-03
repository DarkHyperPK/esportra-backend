namespace Esportra.Api.Services.ProviderMatches.Career;

public sealed record MultiKills(int K2, int K3, int K4, int K5);

public sealed record PlayerTotals(
    int Matches, int ScrimMatches, int TournamentMatches, int Wins, int Losses, int Draws, double? WinRatePct, int Rounds,
    int Kills, int Deaths, int Assists, double? Kd, double? Kda, double? Acs, double? Adr, double? HeadshotSharePct,
    double? KillsPerRound, double? DeathsPerRound, double? AssistsPerRound, int FirstKills, int FirstDeaths,
    MultiKills MultiKills, int Plants, int Defuses);

public sealed record AgentLine(string CharacterId, int Matches, int Wins, double? WinRatePct, double? Kd, double? Acs);
public sealed record MapLine(string MapId, string Map, int Matches, int Wins, double? WinRatePct);
public sealed record TeamLine(Guid? TeamId, string? TeamName, int Matches, int Wins, DateTime? FirstPlayed, DateTime? LastPlayed);
public sealed record MonthLine(string Month, int Matches, int Wins);
public sealed record Sample(int Matches, int Rounds, DateTime? From, DateTime? To);

public sealed record TeamTotals(
    int Matches, int ScrimMatches, int TournamentMatches, int Wins, int Losses, int Draws, double? WinRatePct, int RoundsPlayed,
    int RoundsWon, int RoundsLost, double? RoundWinRatePct, double? AvgRoundsWon, double? AvgRoundsLost,
    double? KillsPerRound, double? DeathsPerRound, double? DamagePerRound, double? FirstKillRatePct, double? FirstDeathRatePct);

/// <summary>
/// Every career figure is computed here, from rows of verified games, so one definition serves every page.
/// Ratios are weighted over all rounds (not an average of per-match values) and are null when the denominator is zero.
/// </summary>
public static class CareerMath
{
    public const string Win = "win";
    public const string Loss = "loss";
    public const string Draw = "draw";

    public static string Result(int? won, int? lost) => (won, lost) switch
    {
        (null, _) or (_, null) => Draw,
        var (w, l) when w > l => Win,
        var (w, l) when w < l => Loss,
        _ => Draw,
    };

    private static double? Ratio(double n, double d, int digits = 2) => d <= 0 ? null : Math.Round(n / d, digits);
    private static double? Pct(double n, double d) => d <= 0 ? null : Math.Round(100.0 * n / d, 1);

    public static Sample SampleOf(IReadOnlyCollection<PlayerMatchRow> rows) =>
        new(rows.Count, rows.Sum(r => r.RoundsPlayed), rows.Min(r => r.StartedAt), rows.Max(r => r.StartedAt));

    public static Sample SampleOf(IReadOnlyCollection<TeamMatchRow> rows) =>
        new(rows.Count, rows.Sum(r => r.RoundsPlayed), rows.Min(r => r.StartedAt), rows.Max(r => r.StartedAt));

    public static PlayerTotals Totals(IReadOnlyCollection<PlayerMatchRow> rows)
    {
        var results = rows.Select(r => Result(r.RoundsWon, r.RoundsLost)).ToList();
        var wins = results.Count(x => x == Win);
        var losses = results.Count(x => x == Loss);
        var rounds = rows.Sum(r => r.RoundsPlayed);
        int kills = rows.Sum(r => r.Kills), deaths = rows.Sum(r => r.Deaths), assists = rows.Sum(r => r.Assists);
        var shots = rows.Sum(r => r.Headshots + r.Bodyshots + r.Legshots);
        return new PlayerTotals(
            rows.Count, rows.Count(r => !ContextTypes.IsTournament(r.ContextType)), rows.Count(r => ContextTypes.IsTournament(r.ContextType)),
            wins, losses, results.Count - wins - losses, Pct(wins, rows.Count), rounds, kills, deaths, assists,
            rounds > 0 ? Ratio(kills, Math.Max(deaths, 1)) : null, rounds > 0 ? Ratio(kills + assists, Math.Max(deaths, 1)) : null,
            Ratio(rows.Sum(r => r.Score), rounds, 1), Ratio(rows.Sum(r => r.DamageDealt), rounds, 1),
            Pct(rows.Sum(r => r.Headshots), shots), Ratio(kills, rounds), Ratio(deaths, rounds), Ratio(assists, rounds),
            rows.Sum(r => r.FirstKills), rows.Sum(r => r.FirstDeaths),
            new MultiKills(rows.Sum(r => r.Kills2K), rows.Sum(r => r.Kills3K), rows.Sum(r => r.Kills4K), rows.Sum(r => r.Kills5K)),
            rows.Sum(r => r.Plants), rows.Sum(r => r.Defuses));
    }

    public static IReadOnlyList<AgentLine> ByAgent(IEnumerable<PlayerMatchRow> rows) =>
        rows.Where(r => r.CharacterId is not null)
            .GroupBy(r => r.CharacterId!.ToLowerInvariant())
            .Select(g =>
            {
                var list = g.ToList();
                var wins = list.Count(r => Result(r.RoundsWon, r.RoundsLost) == Win);
                return new AgentLine(g.Key, list.Count, wins, Pct(wins, list.Count),
                    Ratio(list.Sum(r => r.Kills), Math.Max(list.Sum(r => r.Deaths), 1)),
                    Ratio(list.Sum(r => r.Score), list.Sum(r => r.RoundsPlayed), 1));
            })
            .OrderByDescending(a => a.Matches).ThenBy(a => a.CharacterId, StringComparer.Ordinal).ToList();

    public static IReadOnlyList<MapLine> ByMap(IEnumerable<PlayerMatchRow> rows) =>
        ByMap(rows.Select(r => (r.MapId, Result(r.RoundsWon, r.RoundsLost))));

    public static IReadOnlyList<MapLine> ByMap(IEnumerable<TeamMatchRow> rows) =>
        ByMap(rows.Select(r => (r.MapId, Result(r.RoundsWon, r.RoundsLost))));

    private static IReadOnlyList<MapLine> ByMap(IEnumerable<(string? MapId, string Result)> rows) =>
        rows.Where(r => r.MapId is not null)
            .GroupBy(r => r.MapId!)
            .Select(g =>
            {
                var wins = g.Count(r => r.Result == Win);
                return new MapLine(g.Key, ProviderMatchVerifier.DisplayMap(g.Key) ?? g.Key.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? g.Key,
                    g.Count(), wins, Pct(wins, g.Count()));
            })
            .OrderByDescending(m => m.Matches).ThenBy(m => m.Map, StringComparer.Ordinal).ToList();

    public static IReadOnlyList<TeamLine> ByTeam(IEnumerable<PlayerMatchRow> rows) =>
        rows.GroupBy(r => r.TeamId)
            .Select(g => new TeamLine(g.Key, g.Select(r => r.TeamName).FirstOrDefault(n => n is not null), g.Count(),
                g.Count(r => Result(r.RoundsWon, r.RoundsLost) == Win), g.Min(r => r.StartedAt), g.Max(r => r.StartedAt)))
            .OrderByDescending(t => t.LastPlayed).ToList();

    public static IReadOnlyList<MonthLine> Activity(IEnumerable<PlayerMatchRow> rows) =>
        Activity(rows.Select(r => (r.StartedAt, Result(r.RoundsWon, r.RoundsLost))));

    public static IReadOnlyList<MonthLine> Activity(IEnumerable<TeamMatchRow> rows) =>
        Activity(rows.Select(r => (r.StartedAt, Result(r.RoundsWon, r.RoundsLost))));

    private static IReadOnlyList<MonthLine> Activity(IEnumerable<(DateTime? At, string Result)> rows) =>
        rows.Where(r => r.At is not null)
            .GroupBy(r => r.At!.Value.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture))
            .Select(g => new MonthLine(g.Key, g.Count(), g.Count(r => r.Result == Win)))
            .OrderBy(m => m.Month, StringComparer.Ordinal).ToList();

    public static TeamTotals Totals(IReadOnlyCollection<TeamMatchRow> rows)
    {
        var results = rows.Select(r => Result(r.RoundsWon, r.RoundsLost)).ToList();
        var wins = results.Count(x => x == Win);
        var losses = results.Count(x => x == Loss);
        var rounds = rows.Sum(r => r.RoundsPlayed);
        int won = rows.Sum(r => r.RoundsWon ?? 0), lost = rows.Sum(r => r.RoundsLost ?? 0);
        return new TeamTotals(
            rows.Count, rows.Count(r => !ContextTypes.IsTournament(r.ContextType)), rows.Count(r => ContextTypes.IsTournament(r.ContextType)),
            wins, losses, results.Count - wins - losses, Pct(wins, rows.Count), rounds, won, lost, Pct(won, won + lost),
            Ratio(won, rows.Count, 1), Ratio(lost, rows.Count, 1), Ratio(rows.Sum(r => r.Kills), rounds), Ratio(rows.Sum(r => r.Deaths), rounds),
            Ratio(rows.Sum(r => r.DamageDealt), rounds, 1), Pct(rows.Sum(r => r.FirstKills), rounds), Pct(rows.Sum(r => r.FirstDeaths), rounds));
    }

    /// <summary>Newest first, as W / L / D.</summary>
    public static IReadOnlyList<string> Form(IEnumerable<TeamMatchRow> rows, int take = 5) =>
        rows.OrderByDescending(r => r.StartedAt).Take(take)
            .Select(r => Result(r.RoundsWon, r.RoundsLost) switch { Win => "W", Loss => "L", _ => "D" }).ToList();
}
