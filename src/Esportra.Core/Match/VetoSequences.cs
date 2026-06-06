namespace Esportra.Core.Match;

/// <summary>
/// Dynamic veto step sequences derived from pool size, best-of, and game BO1 style.
/// Mirrors frontend <c>sequences.ts</c>.
/// </summary>
public static class VetoSequences
{
    public enum Bo1Style { PureBan, BanPick }

    public sealed record VetoGameConfig(string Game, int MapPoolSize, Bo1Style Bo1Style);

    private static readonly VetoGameConfig ValorantConfig = new("valorant", 7, Bo1Style.BanPick);
    private static readonly VetoGameConfig Cs2Config       = new("cs2", 7, Bo1Style.PureBan);
    private static readonly VetoGameConfig R6Config        = new("r6s", 9, Bo1Style.PureBan);

    /// <summary>Returns game-specific pool size and BO1 style.</summary>
    public static VetoGameConfig GetGameConfig(string game)
    {
        var key = NormalizeGameKey(game);
        return key switch
        {
            "cs2" or "counter-strike 2" or "counter strike 2" => Cs2Config,
            "r6" or "r6s" or "rainbow six siege" or "rainbow six" or "siege" => R6Config,
            _ => ValorantConfig,
        };
    }

    /// <summary>Generate sequence for the given best-of, game, and pool size.</summary>
    public static IReadOnlyList<VetoStep> GetSequence(int bestOf, string game, int poolSize)
    {
        var config = GetGameConfig(game);
        return GenerateSequence(poolSize, bestOf, config.Bo1Style);
    }

    /// <summary>Backward-compatible default: Valorant pool of 7.</summary>
    public static IReadOnlyList<VetoStep> GetSequence(int bestOf)
        => GetSequence(bestOf, "valorant", 7);

    public static VetoStep? GetStep(int bestOf, int actionNumber, string game, int poolSize)
        => GetSequence(bestOf, game, poolSize).FirstOrDefault(s => s.ActionNumber == actionNumber);

    public static VetoStep? GetStep(int bestOf, int actionNumber)
        => GetStep(bestOf, actionNumber, "valorant", 7);

    public static string GetTeamSideForAction(int bestOf, int actionNumber, string game, int poolSize)
        => GetStep(bestOf, actionNumber, game, poolSize)?.Team ?? "T1";

    public static string GetTeamSideForAction(int bestOf, int actionNumber)
        => GetTeamSideForAction(bestOf, actionNumber, "valorant", 7);

    // ── Generators (mirror frontend sequences.ts) ────────────────────────────

    internal static IReadOnlyList<VetoStep> GenerateSequence(int poolSize, int bestOf, Bo1Style bo1Style)
    {
        if (bestOf == 1) return GenerateBo1(poolSize, bo1Style);
        if (bestOf is 3 or 5) return GenerateBoX(poolSize, bestOf);
        return GenerateBo1(poolSize, bo1Style);
    }

    private static List<VetoStep> GenerateBo1(int poolSize, Bo1Style style)
    {
        var steps = new List<VetoStep>();
        var n = 1;

        if (style == Bo1Style.PureBan)
        {
            for (var i = 0; i < poolSize - 1; i++)
                steps.Add(new(n++, "ban", i % 2 == 0 ? "T1" : "T2"));
            steps.Add(new(n, "pick_side", "T1", IsDecider: true));
        }
        else
        {
            for (var i = 0; i < poolSize - 2; i++)
                steps.Add(new(n++, "ban", i % 2 == 0 ? "T1" : "T2"));
            steps.Add(new(n++, "pick", "T1"));
            steps.Add(new(n, "pick_side", "T2"));
        }

        return steps;
    }

    private static List<VetoStep> GenerateBoX(int poolSize, int bestOf)
    {
        var steps = new List<VetoStep>();
        var n = 1;
        var explicitPicks = bestOf - 1;
        var remainingBans = poolSize - bestOf - 2;

        if (remainingBans < 0)
            throw new InvalidOperationException(
                $"Map pool ({poolSize}) too small for BO{bestOf}. Need at least {bestOf + 2} maps.");

        steps.Add(new(n++, "ban", "T1"));
        steps.Add(new(n++, "ban", "T2"));

        for (var i = 0; i < explicitPicks; i++)
        {
            var picker = i % 2 == 0 ? "T1" : "T2";
            var sidePicker = picker == "T1" ? "T2" : "T1";
            steps.Add(new(n++, "pick", picker));
            steps.Add(new(n++, "pick_side", sidePicker));
        }

        for (var i = 0; i < remainingBans; i++)
            steps.Add(new(n++, "ban", i % 2 == 0 ? "T1" : "T2"));

        steps.Add(new(n, "pick_side", "T1", IsDecider: true));
        return steps;
    }

    private static string NormalizeGameKey(string game)
        => game.Trim().ToLowerInvariant();
}
