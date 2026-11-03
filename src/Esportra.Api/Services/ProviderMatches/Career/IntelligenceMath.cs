namespace Esportra.Api.Services.ProviderMatches.Career;

public sealed record SideLine(int Played, int Won, double? Pct);

public sealed record MapIntel(
    string MapId, string Map, int Games, int Wins, double? WinRatePct, double? WinLowPct, double? WinHighPct,
    int RoundsWon, int RoundsLost, double? RoundWinRatePct, SideLine Attack, SideLine Defense, DateTime? LastPlayed, string Signal);

public sealed record CompositionMap(string Map, int Games, int Wins);

public sealed record CompositionIntel(
    string[] Agents, int Games, int Wins, double? WinRatePct, double? RoundWinRatePct, SideLine Attack, SideLine Defense,
    IReadOnlyList<CompositionMap> Maps, DateTime? LastPlayed);

public sealed record LineupIntel(
    Guid[] UserIds, string?[] Names, bool Complete, int Games, int Wins, double? WinRatePct, double? RoundWinRatePct, DateTime? LastPlayed);

public sealed record RosterStability(int DistinctLineups, int CompleteLineups, double? MostUsedSharePct, int PlayersUsed, int GamesWithKnownPlayers);

public sealed record SubstituteImpact(int GamesWith, int WinsWith, double? WinRateWithPct, int GamesWithout, int WinsWithout, double? WinRateWithoutPct);

public sealed record RecordLine(int Games, int Wins);

public sealed record TeamRecord(
    int Games, int Wins, int Losses, int Draws, double? WinRatePct, double? WinLowPct, double? WinHighPct, int RoundsWon, int RoundsLost, double? RoundWinRatePct);

public sealed record MonthLine(string Month, int Games, int Wins);

public sealed record PairIntel(string?[] Names, RecordLine Together, RecordLine FirstWithoutSecond, RecordLine SecondWithoutFirst);


/// <summary>
/// Team intelligence from verified games. Everything here is observed, not causal: a composition, lineup or map is never
/// said to be better because of its win rate alone, and no map is called a weakness from a handful of games.
/// </summary>
public static class IntelligenceMath
{
    /// <summary>Fewest games before a map can be called a strength or a weakness.</summary>
    public const int MinMapSample = 5;
    public const int TeamSize = 5;
    private const int MinPairGames = 3;

    public const string Strong = "strong";
    public const string Weak = "weak";
    public const string Typical = "typical";
    public const string Insufficient = "insufficient_sample";

    private static double? Pct(double n, double d) => d <= 0 ? null : Math.Round(100.0 * n / d, 1);
    private static bool Won(TeamGameDetail g) => ResultCode.Of(g.RoundsWon, g.RoundsLost) == ResultCode.Win;
    private static SideLine Side(int played, int won) => new(played, won, Pct(won, played));

    /// <summary>95% Wilson score interval for a win rate, in percent. Null without games.</summary>
    public static (double Low, double High)? Wilson(int wins, int games)
    {
        if (games <= 0) return null;
        const double z = 1.96;
        var p = (double)wins / games;
        var denom = 1 + z * z / games;
        var centre = (p + z * z / (2 * games)) / denom;
        var half = z * Math.Sqrt((p * (1 - p) + z * z / (4.0 * games)) / games) / denom;
        return (Math.Round(100 * Math.Max(0, centre - half), 1), Math.Round(100 * Math.Min(1, centre + half), 1));
    }

    private static string SignalFor(int games, int wins, double overallPct)
    {
        if (games < MinMapSample) return Insufficient;
        var (low, high) = Wilson(wins, games)!.Value;
        return low > overallPct ? Strong : high < overallPct ? Weak : Typical;
    }

    public static IReadOnlyList<MapIntel> Maps(IReadOnlyCollection<TeamGameDetail> games)
    {
        var overall = games.Count == 0 ? 0 : 100.0 * games.Count(Won) / games.Count;
        return games.Where(g => g.MapId is not null).GroupBy(g => g.MapId!)
            .Select(group =>
            {
                var list = group.ToList();
                var wins = list.Count(Won);
                int rw = list.Sum(g => g.RoundsWon ?? 0), rl = list.Sum(g => g.RoundsLost ?? 0);
                var interval = Wilson(wins, list.Count);
                return new MapIntel(
                    group.Key, ValorantMaps.Resolve(group.Key),
                    list.Count, wins, Pct(wins, list.Count), interval?.Low, interval?.High, rw, rl, Pct(rw, rw + rl),
                    Side(list.Sum(g => g.AttackPlayed), list.Sum(g => g.AttackWon)), Side(list.Sum(g => g.DefensePlayed), list.Sum(g => g.DefenseWon)),
                    list.Max(g => g.StartedAt), SignalFor(list.Count, wins, overall));
            })
            .OrderByDescending(m => m.Games).ThenBy(m => m.Map, StringComparer.Ordinal).ToList();
    }

    /// <summary>Pool maps the team has no verified game on, so "least practised" includes maps never played.</summary>
    public static IReadOnlyList<string> Unplayed(IEnumerable<string> pool, IEnumerable<MapIntel> played)
    {
        var seen = played.Select(m => m.Map).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return pool.Where(p => !seen.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<CompositionIntel> Compositions(IReadOnlyCollection<TeamGameDetail> games) =>
        games.Where(g => g.Agents.Length == TeamSize)
            .GroupBy(g => string.Join(',', g.Agents.Select(a => a.ToLowerInvariant()).OrderBy(a => a, StringComparer.Ordinal)))
            .Select(group =>
            {
                var list = group.ToList();
                var wins = list.Count(Won);
                int rw = list.Sum(g => g.RoundsWon ?? 0), rl = list.Sum(g => g.RoundsLost ?? 0);
                var maps = list.Where(g => g.MapId is not null).GroupBy(g => ValorantMaps.Resolve(g.MapId))
                    .Select(m => new CompositionMap(m.Key, m.Count(), m.Count(Won)))
                    .OrderByDescending(m => m.Games).ThenBy(m => m.Map, StringComparer.Ordinal).ToList();
                return new CompositionIntel(
                    group.Key.Split(','), list.Count, wins, Pct(wins, list.Count), Pct(rw, rw + rl),
                    Side(list.Sum(g => g.AttackPlayed), list.Sum(g => g.AttackWon)), Side(list.Sum(g => g.DefensePlayed), list.Sum(g => g.DefenseWon)),
                    maps, list.Max(g => g.StartedAt));
            })
            .OrderByDescending(c => c.Games).ThenByDescending(c => c.LastPlayed).ToList();

    private static Dictionary<Guid, List<GamePlayer>> PlayersByMatch(IEnumerable<GamePlayer> players) =>
        players.GroupBy(p => p.MatchId).ToDictionary(g => g.Key, g => g.DistinctBy(p => p.UserId).ToList());

    public static IReadOnlyList<LineupIntel> Lineups(IReadOnlyCollection<TeamGameDetail> games, IEnumerable<GamePlayer> players)
    {
        var byMatch = PlayersByMatch(players);
        return games.Where(g => byMatch.ContainsKey(g.MatchId))
            .GroupBy(g => string.Join(',', byMatch[g.MatchId].Select(p => p.UserId).OrderBy(id => id)))
            .Select(group =>
            {
                var list = group.ToList();
                var roster = byMatch[list[0].MatchId].OrderBy(p => p.UserId).ToList();
                int rw = list.Sum(g => g.RoundsWon ?? 0), rl = list.Sum(g => g.RoundsLost ?? 0);
                var wins = list.Count(Won);
                return new LineupIntel(
                    roster.Select(p => p.UserId).ToArray(), roster.Select(p => p.Username).ToArray(), roster.Count == TeamSize,
                    list.Count, wins, Pct(wins, list.Count), Pct(rw, rw + rl), list.Max(g => g.StartedAt));
            })
            .OrderByDescending(l => l.Games).ThenByDescending(l => l.LastPlayed).ToList();
    }

    public static RosterStability Stability(IReadOnlyCollection<LineupIntel> lineups, IEnumerable<GamePlayer> players)
    {
        var known = lineups.Sum(l => l.Games);
        var complete = lineups.Where(l => l.Complete).ToList();
        return new RosterStability(
            lineups.Count, complete.Count, complete.Count == 0 ? null : Pct(complete.Max(l => l.Games), known),
            players.Select(p => p.UserId).Distinct().Count(), known);
    }

    public static SubstituteImpact Substitutes(IReadOnlyCollection<TeamGameDetail> games, IEnumerable<GamePlayer> players)
    {
        var byMatch = PlayersByMatch(players);
        var known = games.Where(g => byMatch.ContainsKey(g.MatchId)).ToList();
        bool WithSub(TeamGameDetail g) => byMatch[g.MatchId].Any(p => string.Equals(p.MemberRole, "substitute", StringComparison.OrdinalIgnoreCase));
        var with = known.Where(WithSub).ToList();
        var without = known.Where(g => !WithSub(g)).ToList();
        return new SubstituteImpact(with.Count, with.Count(Won), Pct(with.Count(Won), with.Count), without.Count, without.Count(Won), Pct(without.Count(Won), without.Count));
    }

    /// <summary>Pairs of players with at least three games together, with each one's record when the other was out.</summary>
    public static IReadOnlyList<PairIntel> Pairs(IReadOnlyCollection<TeamGameDetail> games, IEnumerable<GamePlayer> players, int take = 12)
    {
        var byMatch = PlayersByMatch(players);
        var known = games.Where(g => byMatch.ContainsKey(g.MatchId)).ToList();
        var names = players.GroupBy(p => p.UserId).ToDictionary(g => g.Key, g => g.Select(p => p.Username).FirstOrDefault(n => n is not null));
        var frequent = byMatch.Values.SelectMany(v => v).GroupBy(p => p.UserId).Where(g => g.Count() >= MinPairGames).Select(g => g.Key).OrderBy(id => id).ToList();

        RecordLine Record(Func<HashSet<Guid>, bool> inGame)
        {
            var hit = known.Where(g => inGame(byMatch[g.MatchId].Select(p => p.UserId).ToHashSet())).ToList();
            return new RecordLine(hit.Count, hit.Count(Won));
        }

        var result = new List<PairIntel>();
        for (var i = 0; i < frequent.Count; i++)
            for (var j = i + 1; j < frequent.Count; j++)
            {
                Guid a = frequent[i], b = frequent[j];
                var together = Record(s => s.Contains(a) && s.Contains(b));
                if (together.Games < MinPairGames) continue;
                result.Add(new PairIntel([names[a], names[b]], together, Record(s => s.Contains(a) && !s.Contains(b)), Record(s => s.Contains(b) && !s.Contains(a))));
            }
        return result.OrderByDescending(p => p.Together.Games).ThenByDescending(p => p.Together.Wins).Take(take).ToList();
    }

    public static TeamRecord Record(IReadOnlyCollection<TeamGameDetail> games)
    {
        var results = games.Select(g => ResultCode.Of(g.RoundsWon, g.RoundsLost)).ToList();
        var wins = results.Count(r => r == ResultCode.Win);
        var losses = results.Count(r => r == ResultCode.Loss);
        int rw = games.Sum(g => g.RoundsWon ?? 0), rl = games.Sum(g => g.RoundsLost ?? 0);
        var interval = Wilson(wins, games.Count);
        return new TeamRecord(games.Count, wins, losses, games.Count - wins - losses, Pct(wins, games.Count), interval?.Low, interval?.High, rw, rl, Pct(rw, rw + rl));
    }

    /// <summary>The last results, newest first, as W / L / D.</summary>
    public static IReadOnlyList<string> Form(IEnumerable<TeamGameDetail> games, int take = 5) =>
        games.OrderByDescending(g => g.StartedAt).Take(take)
            .Select(g => ResultCode.Of(g.RoundsWon, g.RoundsLost) switch { ResultCode.Win => "W", ResultCode.Loss => "L", _ => "D" }).ToList();

    public static IReadOnlyList<MonthLine> Activity(IEnumerable<TeamGameDetail> games) =>
        games.Where(g => g.StartedAt is not null)
            .GroupBy(g => g.StartedAt!.Value.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture))
            .Select(m => new MonthLine(m.Key, m.Count(), m.Count(g => ResultCode.Of(g.RoundsWon, g.RoundsLost) == ResultCode.Win)))
            .OrderBy(m => m.Month, StringComparer.Ordinal).ToList();
}
