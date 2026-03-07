namespace Esportra.Core.Match;

/// <summary>
/// Defines the veto step sequences for BO1, BO3, and BO5.
/// Both Valorant and CS2 share the same sequences (only map pool differs).
/// </summary>
public static class VetoSequences
{
    private static readonly VetoStep[] Bo1 =
    [
        new(1, "ban",       "T1"),
        new(2, "ban",       "T2"),
        new(3, "ban",       "T1"),
        new(4, "ban",       "T2"),
        new(5, "ban",       "T1"),
        new(6, "pick",      "T1"),
        new(7, "pick_side", "T2"),
    ];

    private static readonly VetoStep[] Bo3 =
    [
        new(1, "ban",       "T1"),
        new(2, "ban",       "T2"),
        new(3, "pick",      "T1"),
        new(4, "pick_side", "T2"),
        new(5, "pick",      "T2"),
        new(6, "pick_side", "T1"),
        new(7, "ban",       "T2"),
        new(8, "ban",       "T1"),
        new(9, "pick_side", "T1", IsDecider: true),
    ];

    private static readonly VetoStep[] Bo5 =
    [
        new(1,  "ban",       "T1"),
        new(2,  "ban",       "T2"),
        new(3,  "pick",      "T1"),
        new(4,  "pick_side", "T2"),
        new(5,  "pick",      "T2"),
        new(6,  "pick_side", "T1"),
        new(7,  "pick",      "T1"),
        new(8,  "pick_side", "T2"),
        new(9,  "pick",      "T2"),
        new(10, "pick_side", "T1"),
        new(11, "pick_side", "T1", IsDecider: true),
    ];

    public static IReadOnlyList<VetoStep> GetSequence(int bestOf) => bestOf switch
    {
        3 => Bo3,
        5 => Bo5,
        _ => Bo1,
    };

    /// <summary>Returns the step for the given 1-based action number.</summary>
    public static VetoStep? GetStep(int bestOf, int actionNumber)
        => GetSequence(bestOf).FirstOrDefault(s => s.ActionNumber == actionNumber);

    /// <summary>Returns which team should act at the given action number.</summary>
    public static string GetTeamSideForAction(int bestOf, int actionNumber)
        => GetStep(bestOf, actionNumber)?.Team ?? "T1";
}
