namespace Esportra.Api.Services.ScrimInsights;

/// <summary>
/// KAST: the share of rounds in which a player got a Kill, an Assist, Survived, or was Traded (died, and a teammate killed
/// their killer within 5 seconds). Worked out from the recorded kill feed only.
/// </summary>
internal static class KastMath
{
    public const int TradeWindowMs = 5000;

    public static double? Kast(InMatch m, InPlayer player)
    {
        if (m.Rounds.Count == 0) return null;
        var counted = m.Rounds.Count(r => Counts(m, player, m.Kills.Where(k => k.Round == r.Num).ToList()));
        return InsightsCommon.Pct(counted, m.Rounds.Count);
    }

    private static bool Counts(InMatch m, InPlayer player, List<InKill> kills)
    {
        if (kills.Any(k => k.Killer == player.Puuid || k.Assistants.Contains(player.Puuid))) return true;
        var death = kills.FirstOrDefault(k => k.Victim == player.Puuid);
        if (death is null) return true; // survived
        return kills.Any(t => t.Victim == death.Killer && t.Killer is not null && t.Killer != player.Puuid
            && InsightsCommon.SideOf(m, t.Killer) == player.Side
            && t.RoundMs is { } at && death.RoundMs is { } died && at >= died && at - died <= TradeWindowMs);
    }
}
