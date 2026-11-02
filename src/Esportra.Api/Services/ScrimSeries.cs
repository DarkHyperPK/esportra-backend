namespace Esportra.Api.Services;

/// <summary>One map of a scrim series as the series rules see it.</summary>
public sealed record ScrimGameState(int GameNumber, string Status, int? WinnerSlot);

/// <summary>
/// Rules for a scrim series reported map by map. A map counts once it is confirmed (or resolved by
/// staff); a disputed map does not hold up the others. Pure so the rules are unit-tested.
/// </summary>
public static class ScrimSeries
{
    public const string Reported = "reported";
    public const string Confirmed = "confirmed";
    public const string Disputed = "disputed";
    public const string Resolved = "resolved";

    /// <summary>How long the other team has to confirm or dispute a reported map.</summary>
    public const int DefaultAcceptWindowMinutes = 120;

    public static DateTime AcceptDeadline(DateTime reportedAtUtc, int windowMinutes) =>
        reportedAtUtc.AddMinutes(Math.Max(5, windowMinutes));

    public static bool IsDue(string status, DateTime? acceptBy, DateTime nowUtc) =>
        status == Reported && acceptBy is { } deadline && deadline <= nowUtc;

    public static int WinsNeeded(int bestOf) => (bestOf + 1) / 2;

    /// <summary>1 or 2 for the map's winner, null for a draw (never a valid report).</summary>
    public static int? WinnerSlot(int team1Score, int team2Score) =>
        team1Score > team2Score ? 1 : team2Score > team1Score ? 2 : null;

    private static bool Counts(ScrimGameState g) => g.Status is Confirmed or Resolved;

    public static (int Team1, int Team2) Wins(IEnumerable<ScrimGameState> games)
    {
        var settled = games.Where(g => Counts(g) && g.WinnerSlot is not null).ToList();
        return (settled.Count(g => g.WinnerSlot == 1), settled.Count(g => g.WinnerSlot == 2));
    }

    /// <summary>The team (1 or 2) that has won the series, or null while it is still open.</summary>
    public static int? SeriesWinner(int bestOf, IEnumerable<ScrimGameState> games)
    {
        var (t1, t2) = Wins(games);
        var need = WinsNeeded(bestOf);
        return t1 >= need ? 1 : t2 >= need ? 2 : null;
    }

    /// <summary>A voided (no contest) map is replayed, so the series may run past its nominal length.</summary>
    public static int MaxGameNumber(int bestOf, IEnumerable<ScrimGameState> games) =>
        bestOf + games.Count(g => g.Status == Resolved && g.WinnerSlot is null);

    /// <summary>Why this map cannot be reported right now, or null when it can.</summary>
    public static string? ValidateReport(int bestOf, int gameNumber, IReadOnlyList<ScrimGameState> games, int team1Score, int team2Score)
    {
        if (team1Score < 0 || team2Score < 0) return "Scores cannot be negative.";
        if (team1Score == team2Score) return "A map cannot end in a draw.";
        if (SeriesWinner(bestOf, games) is not null) return "This series is already decided.";
        if (gameNumber < 1 || gameNumber > MaxGameNumber(bestOf, games))
            return $"Game {gameNumber} is not part of this best-of-{bestOf}.";
        if (gameNumber > 1)
        {
            var previous = games.FirstOrDefault(g => g.GameNumber == gameNumber - 1);
            if (previous is null) return $"Report game {gameNumber - 1} first.";
            if (previous.Status == Reported) return $"Game {gameNumber - 1} still needs the opponent's confirmation.";
        }
        return null;
    }

    /// <summary>The map a team should report next, or null when the series is decided.</summary>
    public static int? NextGameNumber(int bestOf, IReadOnlyList<ScrimGameState> games)
    {
        if (SeriesWinner(bestOf, games) is not null) return null;
        var max = MaxGameNumber(bestOf, games);
        for (var n = 1; n <= max; n++)
        {
            var g = games.FirstOrDefault(x => x.GameNumber == n);
            if (g is null) return n;
            if (g.Status == Reported) return n;
        }
        return null;
    }
}
