namespace Esportra.Core.Tournaments;

/// <summary>
/// Per-team standings row. Sealed class with init properties (not record) so
/// HybridCache can serialize it via System.Text.Json's default constructor.
/// </summary>
public sealed class StandingsRow
{
    public int Rank { get; init; }
    public string RankStatus { get; init; } = "active"; // "active" | "confirmed" | "provisional"
    public bool IsTied { get; init; }
    public Guid TeamId { get; init; }
    public string TeamName { get; init; } = string.Empty;
    public string? BracketSide { get; init; }    // "winners" | "losers" | null (DE only)
    public int Played { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public int Ties { get; init; }
    public int ScoreDiff { get; init; }
    public int Points { get; init; }             // RR, Swiss, BR; 0 for SE/DE
    public int Buchholz { get; init; }           // Swiss only; 0 for other formats
    public IReadOnlyList<string>? RoundResults { get; init; }  // Swiss: ["win","loss","win"] per round
    public long Kills { get; init; }             // BR only; 0 for other formats
    public decimal PrizeAmount { get; init; }    // set by StandingsResolutionService
    public string? PlacementLabel { get; init; } // set by StandingsResolutionService
    public bool IsLive { get; init; }            // set by StandingsResolutionService
    public string? LiveOpponent { get; init; }   // set by StandingsResolutionService
}

/// <summary>
/// Full standings envelope cached in HybridCache. Same sealed-class-with-init constraint.
/// </summary>
public sealed class TournamentStandingsResponse
{
    public string Format { get; init; } = string.Empty;
    public bool IsComplete { get; init; }
    public DateTimeOffset ComputedAt { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<StandingsRow> Rows { get; init; } = [];
}
