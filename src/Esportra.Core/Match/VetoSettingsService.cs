using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Core.Match;

public sealed class VetoSettingsService(IVetoSettingsRepository settingsRepo, IDbConnectionFactory db)
{
    private sealed record VetoConfigRow(int BestOf, string? Game, string Status);

    public async Task<VetoSettingsDto?> GetAsync(Guid matchId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var config = await conn.QuerySingleOrDefaultAsync<VetoConfigRow>(
            "SELECT best_of, game, status FROM public.match_map_vetos WHERE match_id = @matchId",
            new { matchId });

        if (config is null) return null;

        var game = config.Game ?? "valorant";
        var poolSize = VetoSequences.GetGameConfig(game).MapPoolSize;
        var defaultSequence = VetoSequences.GetSequence(config.BestOf, game, poolSize);

        var settings = await settingsRepo.GetAsync(matchId, ct);
        var resolver = VetoSequenceResolverFactory.Create(settings);
        var stub = new MatchMapVeto { BestOf = config.BestOf, Game = game, SelectedMapPool = [] };
        var effectiveSequence = resolver.Resolve(stub, poolSize);

        return new VetoSettingsDto(
            matchId,
            settings?.Mode ?? VetoMode.Default,
            defaultSequence,
            settings?.Sequence,
            effectiveSequence);
    }

    public async Task SaveAsync(
        Guid matchId, VetoMode mode, VetoStep[]? sequence, Guid updatedBy, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var config = await conn.QuerySingleOrDefaultAsync<VetoConfigRow>(
            "SELECT best_of, game, status FROM public.match_map_vetos WHERE match_id = @matchId",
            new { matchId });

        if (config is null)
            throw new InvalidOperationException("INVALID_SEQUENCE: veto not found.");

        if (config.Status == "in_progress")
            throw new InvalidOperationException("CONFLICT: veto is in_progress");

        if (mode == VetoMode.Custom)
            ValidateCustomSequence(sequence, config);

        var storedSequence = mode == VetoMode.Custom ? sequence : null;
        await settingsRepo.SaveAsync(new VetoSettings(matchId, mode, storedSequence), updatedBy, ct);
    }

    public async Task ClearAsync(Guid matchId, CancellationToken ct = default)
        => await settingsRepo.ClearAsync(matchId, ct);

    private static void ValidateCustomSequence(VetoStep[]? sequence, VetoConfigRow config)
    {
        if (sequence is null || sequence.Length == 0)
            throw new InvalidOperationException("INVALID_SEQUENCE: sequence must be non-empty for custom mode.");

        ValidateActionNumbers(sequence);
        ValidateStepActions(sequence);
        ValidateDeciderCount(sequence);
        ValidatePicksForFormat(sequence, config.BestOf);
        ValidateSequenceLength(sequence, config);
    }

    internal static void ValidateActionNumbers(VetoStep[] sequence)
    {
        for (int i = 0; i < sequence.Length; i++)
        {
            if (sequence[i].ActionNumber != i + 1)
                throw new InvalidOperationException(
                    $"INVALID_SEQUENCE: action numbers must be consecutive from 1. Expected {i + 1}, got {sequence[i].ActionNumber}.");
        }
    }

    internal static void ValidateStepActions(VetoStep[] sequence)
    {
        var validActions = new HashSet<string> { "ban", "pick", "pick_side", "ignore" };
        var validTeams = new HashSet<string> { "T1", "T2" };

        foreach (var step in sequence)
        {
            if (!validActions.Contains(step.Action))
                throw new InvalidOperationException(
                    $"INVALID_SEQUENCE: invalid action '{step.Action}'. Valid: ban, pick, pick_side, ignore.");

            if (step.Action != "ignore" && !validTeams.Contains(step.Team))
                throw new InvalidOperationException(
                    $"INVALID_SEQUENCE: invalid team '{step.Team}' for action '{step.Action}'.");
        }

        if (sequence.Last().Action == "ignore")
            throw new InvalidOperationException("INVALID_SEQUENCE: last step cannot be 'ignore'.");
    }

    internal static void ValidateDeciderCount(VetoStep[] sequence)
    {
        var deciderCount = sequence.Count(s => s.IsDecider);
        if (deciderCount != 1)
            throw new InvalidOperationException(
                $"INVALID_SEQUENCE: exactly one step must be IsDecider. Found {deciderCount}.");
    }

    internal static void ValidatePicksForFormat(VetoStep[] sequence, int bestOf)
    {
        if (bestOf is 1) return;
        var pickCount = sequence.Count(s => s.Action == "pick");
        var expectedPicks = bestOf - 1;
        if (pickCount != expectedPicks)
            throw new InvalidOperationException(
                $"INVALID_SEQUENCE: BO{bestOf} requires exactly {expectedPicks} pick steps, got {pickCount}.");
    }

    internal static void ValidateSequenceLength(VetoStep[] sequence, string game, int bestOf)
    {
        var poolSize = VetoSequences.GetGameConfig(game).MapPoolSize;
        var expectedCount = VetoSequences.GetSequence(bestOf, game, poolSize).Count;

        if (sequence.Length != expectedCount)
            throw new InvalidOperationException(
                $"INVALID_SEQUENCE: expected {expectedCount} steps for BO{bestOf} {game}, got {sequence.Length}.");
    }

    private static void ValidateSequenceLength(VetoStep[] sequence, VetoConfigRow config)
        => ValidateSequenceLength(sequence, config.Game ?? "valorant", config.BestOf);
}
