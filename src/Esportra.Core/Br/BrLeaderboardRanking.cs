namespace Esportra.Core.Br;

public enum BrTiebreaker
{
    MostWins,
    MostKills,
    HeadToHead,
}

public sealed record BrLeaderboardAggregate(
    long TotalPoints,
    long Wins,
    long TotalKills,
    double AvgPlacement);

/// <summary>
/// Deterministic BR leaderboard ordering — mirrored by frontend brConfigResolve.ts.
/// </summary>
public static class BrLeaderboardRanking
{
    public static int Compare(BrLeaderboardAggregate a, BrLeaderboardAggregate b, BrTiebreaker tiebreaker)
    {
        var pointsComparison = b.TotalPoints.CompareTo(a.TotalPoints);
        if (pointsComparison != 0)
            return pointsComparison;

        return tiebreaker switch
        {
            BrTiebreaker.MostWins => CompareMostWins(a, b),
            BrTiebreaker.MostKills => CompareMostKills(a, b),
            BrTiebreaker.HeadToHead => CompareHeadToHead(a, b),
            _ => CompareMostWins(a, b),
        };
    }

    private static int CompareMostWins(BrLeaderboardAggregate a, BrLeaderboardAggregate b)
    {
        var winsComparison = b.Wins.CompareTo(a.Wins);
        if (winsComparison != 0)
            return winsComparison;

        var killsComparison = b.TotalKills.CompareTo(a.TotalKills);
        if (killsComparison != 0)
            return killsComparison;

        return a.AvgPlacement.CompareTo(b.AvgPlacement);
    }

    private static int CompareMostKills(BrLeaderboardAggregate a, BrLeaderboardAggregate b)
    {
        var killsComparison = b.TotalKills.CompareTo(a.TotalKills);
        if (killsComparison != 0)
            return killsComparison;

        var winsComparison = b.Wins.CompareTo(a.Wins);
        if (winsComparison != 0)
            return winsComparison;

        return a.AvgPlacement.CompareTo(b.AvgPlacement);
    }

    private static int CompareHeadToHead(BrLeaderboardAggregate a, BrLeaderboardAggregate b)
    {
        var placementComparison = a.AvgPlacement.CompareTo(b.AvgPlacement);
        if (placementComparison != 0)
            return placementComparison;

        var winsComparison = b.Wins.CompareTo(a.Wins);
        if (winsComparison != 0)
            return winsComparison;

        return b.TotalKills.CompareTo(a.TotalKills);
    }
}
