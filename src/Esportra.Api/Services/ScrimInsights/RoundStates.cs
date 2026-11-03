namespace Esportra.Api.Services.ScrimInsights;

/// <summary>Who was alive after each kill of a round, on each side. Built from the kill feed; players that did not appear on the match's player list are not counted.</summary>
internal sealed record RoundStep(InKill Kill, bool VictimMine, int MineAlive, int TheirsAlive, string? ClutchPuuid);

internal static class RoundStates
{
    public const int TradeWindowMs = KastMath.TradeWindowMs;

    public static IReadOnlyList<RoundStep> Steps(InMatch m, int round)
    {
        var mine = m.Players.Where(p => p.Side == m.MySide).Select(p => p.Puuid).ToHashSet();
        var theirs = m.Players.Where(p => p.Side != m.MySide).Select(p => p.Puuid).ToHashSet();
        var steps = new List<RoundStep>();
        var clutchSeen = false;
        foreach (var k in m.Kills.Where(k => k.Round == round).OrderBy(k => k.RoundMs ?? int.MaxValue).ThenBy(k => k.Ordinal))
        {
            var victimMine = mine.Remove(k.Victim);
            if (!victimMine) theirs.Remove(k.Victim);
            string? clutch = null;
            if (!clutchSeen && mine.Count == 1 && theirs.Count >= 1) { clutch = mine.First(); clutchSeen = true; }
            steps.Add(new RoundStep(k, victimMine, mine.Count, theirs.Count, clutch));
        }
        return steps;
    }

    /// <summary>True when a teammate of the victim killed the victim's killer within the trade window.</summary>
    public static bool IsTraded(InMatch m, InKill death)
    {
        if (death.Killer is null || death.RoundMs is not { } at) return false;
        var side = InsightsCommon.SideOf(m, death.Victim);
        return m.Kills.Any(k => k.Round == death.Round && k.Victim == death.Killer && k.Killer is not null && k.Killer != death.Victim
            && InsightsCommon.SideOf(m, k.Killer) == side && k.RoundMs is { } t && t >= at && t - at <= TradeWindowMs);
    }

    /// <summary>True when the killed player had killed one of the killer's teammates within the window just before.</summary>
    public static bool IsTradeKill(InMatch m, InKill kill)
    {
        if (kill.Killer is null || kill.RoundMs is not { } at) return false;
        var side = InsightsCommon.SideOf(m, kill.Killer);
        return m.Kills.Any(k => k.Round == kill.Round && k.Killer == kill.Victim && InsightsCommon.SideOf(m, k.Victim) == side
            && k.RoundMs is { } t && t <= at && at - t <= TradeWindowMs);
    }
}
