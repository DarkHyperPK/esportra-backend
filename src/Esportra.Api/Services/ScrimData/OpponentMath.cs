namespace Esportra.Api.Services.ScrimData;

public sealed record Meeting(Guid SessionId, int GameNumber, DateTime? PlayedAt, string? Map, string Result, int? RoundsWon, int? RoundsLost, string Tier, string[] Agents);

public sealed record OpponentMap(string Map, int Games, int Wins);

/// <summary>
/// One scrim partner as a team: how the maps went against them, which maps, the vetoes and what your team played. Team-level only: no player of theirs
/// appears, is named or is counted across matches here.
/// </summary>
public sealed record OpponentRow(
    Guid OpponentId, string Name, int Maps, int Wins, int Losses, double? WinRatePct, int RoundsWon, int RoundsLost, double? RoundWinPct,
    DateTime? FirstPlayed, DateTime? LastPlayed, IReadOnlyList<OpponentMap> MapsPlayed, IReadOnlyList<Meeting> Meetings, MapPicks Veto);

public static class OpponentMath
{
    private static double? Pct(int part, int whole) => whole > 0 ? Math.Round(100.0 * part / whole, 1) : null;

    public const int MeetingCap = 12;

    /// <summary>Opponents by maps played, most first. The veto read for each uses only the vetoes and maps against that team.</summary>
    public static IReadOnlyList<OpponentRow> Build(
        IReadOnlyList<ScrimGame> games, IReadOnlyList<VetoSessionRow> vetoes, IReadOnlyList<VetoActionRow> actions, IReadOnlyList<VetoGameRow> vetoGames)
    {
        return games.Where(g => g.Row.OpponentId is not null).GroupBy(g => g.Row.OpponentId!.Value).Select(group =>
        {
            var list = group.OrderByDescending(g => g.Row.PlayedAt ?? DateTime.MinValue).ToList();
            var wins = list.Count(g => g.Result == "win");
            var losses = list.Count(g => g.Result == "loss");
            int rw = list.Sum(g => g.Detail.RoundsWon ?? 0), rl = list.Sum(g => g.Detail.RoundsLost ?? 0);
            var sessions = vetoes.Where(v => v.OpponentId == group.Key).ToList();
            var sessionIds = sessions.Select(s => s.SessionId).ToHashSet();
            var vetoIds = sessions.Select(s => s.VetoId).ToHashSet();
            return new OpponentRow(
                group.Key, list[0].Row.OpponentName ?? "Opponent", list.Count, wins, losses, Pct(wins, list.Count), rw, rl, Pct(rw, rw + rl),
                list.Min(g => g.Row.PlayedAt), list.Max(g => g.Row.PlayedAt),
                list.Where(g => g.Map is not null).GroupBy(g => g.Map!).Select(m => new OpponentMap(m.Key, m.Count(), m.Count(g => g.Result == "win")))
                    .OrderByDescending(m => m.Games).ThenBy(m => m.Map, StringComparer.Ordinal).ToList(),
                list.Take(MeetingCap).Select(g => new Meeting(g.Row.SessionId, g.Row.GameNumber, g.Row.PlayedAt, g.Map, g.Result, g.Detail.RoundsWon, g.Detail.RoundsLost, g.Tier,
                    g.Detail.Agents)).ToList(),
                MapPickMath.Build(sessions, actions.Where(a => vetoIds.Contains(a.VetoId)).ToList(), vetoGames.Where(v => sessionIds.Contains(v.SessionId)).ToList()));
        }).OrderByDescending(o => o.Maps).ThenBy(o => o.Name, StringComparer.Ordinal).ToList();
    }
}
