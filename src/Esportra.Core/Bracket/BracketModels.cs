namespace Esportra.Core.Bracket;

// ── Bracket Graph Domain Models ────────────────────────────────────────────

public sealed record BracketVersion(
    Guid   Id,
    Guid   TournamentId,
    Guid?  StageId,
    int    VersionNumber,
    string Status,           // draft | active | archived
    string CreatedAt);

public sealed record BracketNode(
    Guid    Id,
    Guid    VersionId,
    int     RoundIndex,
    int     MatchNumber,
    string  BracketType,     // winners | losers | final | group | swiss_round
    string  Status,          // pending | live | completed
    int     BestOf = 1,
    Guid?   Team1Id = null,
    Guid?   Team2Id = null,
    Guid?   WinnerId = null,
    Guid?   LoserId = null,
    int?    Team1Score = null,
    int?    Team2Score = null,
    string? GroupId = null,
    int?    RoundNumber = null,
    string? ScheduledTime = null,
    double? X = null,
    double? Y = null);

public sealed record BracketEdge(
    Guid   Id,
    Guid   VersionId,
    Guid   SourceMatchId,
    Guid   TargetMatchId,
    string Type,            // winner | loser
    int    TargetSlot);     // 1 | 2

public sealed record BracketGraph(
    BracketVersion    Version,
    List<BracketNode> Nodes,
    List<BracketEdge> Edges);

// ── Standings ──────────────────────────────────────────────────────────────

public sealed record TeamStanding(
    Guid   TeamId,
    string TeamName,
    int    Played,
    int    Wins,
    int    Losses,
    int    Ties,
    int    Points,
    int    Buchholz,
    int    ScoreDiff,
    int    Rank);
