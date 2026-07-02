namespace Esportra.Core.Bracket;

/// <summary>
/// Domain model for stage round configuration.
/// Encapsulates per-round settings resolution for bracket generation.
/// </summary>
public sealed class StageRoundConfiguration
{
    private readonly string _format;
    private readonly int _defaultBestOf;
    private readonly string _boMode;
    private readonly IReadOnlyDictionary<string, int>? _overrides;

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
    }

    public int DefaultBestOf => _defaultBestOf;

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
            Console.WriteLine($"[GetBestOf] Mode=per_stage, returning default={_defaultBestOf}");
            return _defaultBestOf;
        }

        if (_overrides is null || _overrides.Count == 0)
        {
            Console.WriteLine($"[GetBestOf] Mode=per_round but no overrides, returning default={_defaultBestOf}");
            return _defaultBestOf;
        }

        var key = ResolveRoundKey(roundIndex, bracketType, totalRoundsInBracket);
        var found = _overrides.TryGetValue(key, out var bo);
        Console.WriteLine($"[GetBestOf] roundIndex={roundIndex}, bracketType={bracketType}, key={key}, found={found}, value={bo}, default={_defaultBestOf}");
        return found ? bo : _defaultBestOf;
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
