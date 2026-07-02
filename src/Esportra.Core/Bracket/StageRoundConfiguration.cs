using Microsoft.Extensions.Logging;

namespace Esportra.Core.Bracket;

/// <summary>
/// Domain model for stage round configuration.
/// Encapsulates per-round settings resolution for bracket generation.
/// </summary>
public sealed class StageRoundConfiguration
{
    private static ILogger? s_logger;

    private readonly string _format;
    private readonly int _defaultBestOf;
    private readonly string _boMode;
    private readonly IReadOnlyDictionary<string, int>? _overrides;

    /// <summary>
    /// Configures logging for StageRoundConfiguration.
    /// Call once at application startup.
    /// </summary>
    public static void ConfigureLogging(ILoggerFactory loggerFactory)
    {
        s_logger = loggerFactory.CreateLogger<StageRoundConfiguration>();
        s_logger.LogInformation("StageRoundConfiguration logging configured");
    }

    public StageRoundConfiguration(
        string format,
        int defaultBestOf,
        string boMode = "per_stage",
        IReadOnlyDictionary<string, int>? roundBoOverrides = null)
    {
        _format = format.ToLowerInvariant();
        _defaultBestOf = defaultBestOf;
        _boMode = boMode;
        _overrides = roundBoOverrides;

        // Fail loudly if per_round mode but no overrides provided
        if (_boMode == "per_round" && (_overrides is null || _overrides.Count == 0))
        {
            var error = "Per-round BO mode requires at least one round override. " +
                        "Either provide roundBoOverrides or use 'per_stage' mode.";
            s_logger?.LogError("StageRoundConfiguration validation failed: {Error}", error);
            throw new ArgumentException(error, nameof(roundBoOverrides));
        }

        s_logger?.LogInformation(
            "Created StageRoundConfiguration: format={Format}, mode={Mode}, defaultBo={DefaultBo}, overrideCount={Count}",
            _format, _boMode, _defaultBestOf, _overrides?.Count ?? 0);

        if (_overrides is { Count: > 0 })
        {
            s_logger?.LogInformation(
                "Round overrides: {Overrides}",
                string.Join(", ", _overrides.Select(kv => $"{kv.Key}={kv.Value}")));
        }
    }

    public int DefaultBestOf => _defaultBestOf;
    public string BoMode => _boMode;
    public IReadOnlyDictionary<string, int>? Overrides => _overrides;

    /// <summary>
    /// Resolves the best-of value for a specific round.
    /// </summary>
    /// <param name="roundIndex">0-indexed round number within the bracket type</param>
    /// <param name="bracketType">"winners", "losers", or "final"</param>
    /// <param name="totalRoundsInBracket">Total rounds for this bracket type (winners or losers)</param>
    public int GetBestOf(int roundIndex, string bracketType, int totalRoundsInBracket)
    {
        if (_boMode == "per_stage")
        {
            s_logger?.LogDebug(
                "GetBestOf: per_stage mode, returning default={Default}",
                _defaultBestOf);
            return _defaultBestOf;
        }

        // In per_round mode, we validated overrides exist in constructor
        var key = ResolveRoundKey(roundIndex, bracketType, totalRoundsInBracket);
        var found = _overrides!.TryGetValue(key, out var bo);

        if (found)
        {
            s_logger?.LogDebug(
                "GetBestOf: roundIndex={RoundIndex}, bracketType={BracketType}, key={Key}, found override={Value}",
                roundIndex, bracketType, key, bo);
            return bo;
        }
        else
        {
            s_logger?.LogWarning(
                "GetBestOf: roundIndex={RoundIndex}, bracketType={BracketType}, key={Key} NOT FOUND in overrides, using default={Default}",
                roundIndex, bracketType, key, _defaultBestOf);
            return _defaultBestOf;
        }
    }

    private string ResolveRoundKey(int roundIndex, string bracketType, int totalRounds)
    {
        if (_format == "double_elimination")
        {
            return bracketType switch
            {
                "final" => "grand_final",
                "losers" when roundIndex == totalRounds - 1 => "losers_final",
                "losers" => $"losers_round_{roundIndex + 1}",
                "winners" when roundIndex == totalRounds - 1 => "winners_final",
                "winners" => $"winners_round_{roundIndex + 1}",
                _ => $"round_{roundIndex + 1}"
            };
        }

        // Single elimination: last round is "final", others are "round_N"
        return roundIndex == totalRounds - 1 ? "final" : $"round_{roundIndex + 1}";
    }

    /// <summary>
    /// Returns all configurable rounds for a format and bracket size.
    /// Used by frontend to render per-round BO configuration UI.
    /// </summary>
    public static IReadOnlyList<RoundInfo> GetRoundStructure(string format, int bracketSize)
    {
        var rounds = new List<RoundInfo>();
        format = format.ToLowerInvariant();

        int p = (int)Math.Pow(2, Math.Ceiling(Math.Log2(Math.Max(bracketSize, 2))));
        int winnersRounds = (int)Math.Round(Math.Log2(p));

        if (format == "double_elimination")
        {
            int losersRounds = 2 * winnersRounds - 2;
            int order = 1;

            // Winners bracket rounds
            for (int r = 0; r < winnersRounds; r++)
            {
                string key = r == winnersRounds - 1 ? "winners_final" : $"winners_round_{r + 1}";
                string label = r == winnersRounds - 1 ? "Winners Final" : $"Winners Round {r + 1}";
                rounds.Add(new RoundInfo(key, label, order++, "winners"));
            }

            // Losers bracket rounds
            for (int r = 0; r < losersRounds; r++)
            {
                string key = r == losersRounds - 1 ? "losers_final" : $"losers_round_{r + 1}";
                string label = r == losersRounds - 1 ? "Losers Final" : $"Losers Round {r + 1}";
                rounds.Add(new RoundInfo(key, label, order++, "losers"));
            }

            // Grand final
            rounds.Add(new RoundInfo("grand_final", "Grand Final", order, "final"));
        }
        else if (format == "single_elimination")
        {
            for (int r = 0; r < winnersRounds; r++)
            {
                string key = r == winnersRounds - 1 ? "final" : $"round_{r + 1}";
                string label = r == winnersRounds - 1 ? "Final" : $"Round {r + 1}";
                rounds.Add(new RoundInfo(key, label, r + 1, "winners"));
            }
        }

        return rounds;
    }

    /// <summary>
    /// Creates a default per-stage configuration (uniform BO for all matches).
    /// </summary>
    public static StageRoundConfiguration PerStage(string format, int bestOf)
        => new(format, bestOf, "per_stage", null);
}

/// <summary>
/// Describes a configurable round in a bracket format.
/// </summary>
public sealed record RoundInfo(
    string Key,
    string Label,
    int Order,
    string BracketType);
