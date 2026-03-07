namespace Esportra.Core.Bracket;

// ── Bracket Graph Domain Models ────────────────────────────────────────────

public sealed record BracketVersion(
    string Id,
    string TournamentId,
    string? StageId,
    int    VersionNumber,
    string Status,           // draft | active | archived
    string CreatedAt);

public sealed record BracketNode(
    string  Id,
    string  VersionId,
    int     RoundIndex,
    int     MatchNumber,
    string  BracketType,     // winners | losers | final | group | swiss_round
    string  Status,          // pending | live | completed
    int     BestOf = 1,
    string? Team1Id = null,
    string? Team2Id = null,
    string? WinnerId = null,
    string? LoserId = null,
    int?    Team1Score = null,
    int?    Team2Score = null,
    string? GroupId = null,
    int?    RoundNumber = null,
    string? ScheduledTime = null,
    double? X = null,
    double? Y = null);

public sealed record BracketEdge(
    string Id,
    string VersionId,
    string SourceMatchId,
    string TargetMatchId,
    string Type,            // winner | loser
    int    TargetSlot);     // 1 | 2

public sealed record BracketGraph(
    BracketVersion    Version,
    List<BracketNode> Nodes,
    List<BracketEdge> Edges);

// ── Standings ──────────────────────────────────────────────────────────────

public sealed record TeamStanding(
    string TeamId,
    string TeamName,
    int    Played,
    int    Wins,
    int    Losses,
    int    Ties,
    int    Points,
    int    Buchholz,
    int    ScoreDiff,
    int    Rank);
