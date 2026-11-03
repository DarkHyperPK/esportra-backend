using Esportra.Api.Services.ScrimInsights;

namespace Esportra.Api.Services.ScrimReports;

/// <summary>One part of a rating: the measured value, its 0–100 score and its share of the rating.</summary>
public sealed record RatingPart(string Key, string Label, double? Value, double? Score, double Weight);

/// <summary>A 0–100 rating built from parts; null value when there is not enough data. Change = latest window minus the one before.</summary>
public sealed record Rating(double? Value, double? Change, IReadOnlyList<RatingPart> Parts);

public sealed record PlayerRating(Guid UserId, Rating Rating);

public sealed record MapRating(int Index, double? Value);

public sealed record TeamRatings(Rating Team, IReadOnlyList<PlayerRating> Players, IReadOnlyList<MapRating> ByMap);

/// <summary>
/// The Esportra rating. Each part is scaled linearly between a floor (0) and a ceiling (100); small samples are pulled
/// toward a typical value so one lucky clutch or a thin map pool cannot top the chart. Every constant lives here.
///   Player (3+ maps): ACS 35% (150→300), KAST 20% (55→85%), opening duels 20% (35→65%, toward 50% over 4 duels),
///                     deaths traded 15% (10→40%, toward 25% over 6 deaths), clutches 10% (0→40%, toward 20% over 3 tries).
///   Team: round win 40% (40→60%, plus half a map at 50%: +13 / +26 rounds), opening rounds 15% (35→65%),
///         deaths traded 15% (10→40%), won once planted 15% (40→80%), retakes 15% (10→40%).
/// A part with no data is left out and the others share its weight.
/// </summary>
public static class RatingMath
{
    public const int MinPlayerMaps = 3;
    public const int ChangeWindow = 5;

    public static double Scale(double value, double floor, double ceiling) =>
        Math.Round(Math.Clamp((value - floor) / (ceiling - floor), 0, 1) * 100, 1);

    /// <summary>A share pulled toward a typical value: (count + typical × prior) ÷ (of + prior).</summary>
    public static double Shrink(int count, int of, double typical, double prior) => (count + typical * prior) / (of + prior);

    public static Rating Combine(IReadOnlyList<RatingPart> parts)
    {
        var scored = parts.Where(p => p.Score is not null).ToList();
        var weight = scored.Sum(p => p.Weight);
        double? value = weight <= 0 ? null : Math.Round(scored.Sum(p => p.Score!.Value * p.Weight) / weight, 1);
        return new Rating(value, null, parts);
    }

    private static RatingPart Part(string key, string label, double? value, double weight, double floor, double ceiling) =>
        new(key, label, value is null ? null : Math.Round(value.Value, 1), value is null ? null : Scale(value.Value, floor, ceiling), weight);

    private static double? Share(int count, int of, double typical, double prior) => of == 0 ? null : 100 * Shrink(count, of, typical, prior);

    public static Rating Player(PlayerRow p, CoachUserRow? c)
    {
        if (p.Maps < MinPlayerMaps) return new Rating(null, null, []);
        return Combine([
            Part("acs", "ACS", p.Acs, 0.35, 150, 300),
            Part("kast", "KAST %", p.Kast, 0.20, 55, 85),
            Part("opening", "Opening duels won %", c is null ? null : Share(c.EntryWins, c.EntryDuels, 0.5, 4), 0.20, 35, 65),
            Part("traded", "Deaths traded %", c is null ? null : Share(c.Traded, c.Deaths, 0.25, 6), 0.15, 10, 40),
            Part("clutch", "Clutches won %", c is null ? null : Share(c.ClutchWins, c.ClutchAttempts, 0.2, 3), 0.10, 0, 40),
        ]);
    }

    /// <summary>The team rating from round totals and the counts behind each part.</summary>
    public static Rating Team(int roundsWon, int roundsPlayed, Rate firstBloodRounds, TradeCounts trades, Rate postPlant, Rate retake)
    {
        double? roundWin = roundsPlayed == 0 ? null : 100.0 * (roundsWon + 13) / (roundsPlayed + 26);
        return Combine([
            Part("round_win", "Round win %", roundWin, 0.40, 40, 60),
            Part("opening", "Rounds opened %", firstBloodRounds.Of == 0 ? null : 100.0 * firstBloodRounds.Count / firstBloodRounds.Of, 0.15, 35, 65),
            Part("traded", "Deaths traded %", trades.Deaths == 0 ? null : 100.0 * trades.Traded / trades.Deaths, 0.15, 10, 40),
            Part("post_plant", "Won once planted %", postPlant.Of == 0 ? null : 100.0 * postPlant.Count / postPlant.Of, 0.15, 40, 80),
            Part("retake", "Retakes won %", retake.Of == 0 ? null : 100.0 * retake.Count / retake.Of, 0.15, 10, 40),
        ]);
    }

    public static Rating Team(TeamReport r) =>
        Team(r.Series.Sum(s => s.RoundsWon), r.Series.Sum(s => s.RoundsWon + s.RoundsLost), r.FirstBloodRounds, r.Coach.Trades, r.Coach.PostPlant, r.Coach.Retake);

    public static double? Map(ReportMatch m) =>
        Team(m.Game.RoundsWon, m.Game.RoundsWon + m.Game.RoundsLost, m.Report.Opening.FirstBloodRounds, m.Report.Coach.Trades, m.Report.Coach.PostPlant, m.Report.Coach.Retake).Value;

    /// <summary>Everything the overview needs. Matches oldest first; the change compares the latest five maps with the five before.</summary>
    public static TeamRatings Build(TeamReport report, IReadOnlyList<ReportMatch> matches, bool capped)
    {
        var ordered = matches.OrderBy(m => m.Game.StartedAt ?? DateTime.MinValue).ToList();
        var team = Team(report);
        double? teamChange = null;
        Dictionary<Guid, double?> playerChange = [];
        if (ordered.Count >= 2 * ChangeWindow)
        {
            var latest = ScrimReportMath.Team(ordered.TakeLast(ChangeWindow).ToList(), capped);
            var before = ScrimReportMath.Team(ordered.SkipLast(ChangeWindow).TakeLast(ChangeWindow).ToList(), capped);
            teamChange = Diff(Team(latest).Value, Team(before).Value);
        }
        if (ordered.Count >= 2 * MinPlayerMaps)
        {
            var half = ordered.Count / 2;
            var latest = ScrimReportMath.Team(ordered.Skip(half).ToList(), capped);
            var before = ScrimReportMath.Team(ordered.Take(half).ToList(), capped);
            foreach (var p in report.Players)
                playerChange[p.UserId] = Diff(PlayerOf(latest, p.UserId)?.Value, PlayerOf(before, p.UserId)?.Value);
        }
        var players = report.Players.Select(p => new PlayerRating(p.UserId, Player(p, report.Coach.Players.FirstOrDefault(c => c.UserId == p.UserId)) with
        {
            Change = playerChange.GetValueOrDefault(p.UserId),
        })).ToList();
        var byMap = ordered.Select((m, i) => new MapRating(i + 1, Map(m))).ToList();
        return new TeamRatings(team with { Change = teamChange }, players, byMap);
    }

    private static Rating? PlayerOf(TeamReport r, Guid userId) =>
        r.Players.FirstOrDefault(p => p.UserId == userId) is { } p ? Player(p, r.Coach.Players.FirstOrDefault(c => c.UserId == userId)) : null;

    private static double? Diff(double? a, double? b) => a is null || b is null ? null : Math.Round(a.Value - b.Value, 1);
}
