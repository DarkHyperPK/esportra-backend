namespace Esportra.Core.Match;

// ── Veto State Machine Types ───────────────────────────────────────────────

public enum VetoState { Init, Ban, Pick, PickSide, Complete }

public enum VetoEvent { SetBo, BanMap, PickMap, PickSide, Reset }

public sealed record VetoTransitionResult(bool Ok, string? Reason = null);

public sealed record TurnContext(
    Guid?   CurrentTeamId,
    bool    IsOrganizer,
    string? UserTeamId,
    bool    IsCaptain);

// ── DB row model (matches match_map_veto table) ───────────────────────────

public sealed record MatchMapVeto
{
    public Guid    Id                  { get; init; }
    public Guid    MatchId             { get; init; }
    public Guid    TournamentId        { get; init; }
    public Guid?   Team1Id             { get; init; }
    public Guid?   Team2Id             { get; init; }
    public int     BestOf              { get; init; } = 1;
    public string  Status              { get; init; } = "pending"; // pending | in_progress | completed | cancelled
    public Guid?   CurrentTeamId       { get; init; }
    public string? CurrentAction       { get; init; }              // ban | pick | pick_side
    public int     CurrentActionNumber { get; init; }
    public string[]  Team1BannedMaps   { get; init; } = [];
    public string[]  Team2BannedMaps   { get; init; } = [];
    public PickedMap[] Team1PickedMaps { get; init; } = [];
    public PickedMap[] Team2PickedMaps { get; init; } = [];
    public string? SelectedMapId       { get; init; }
    public string[] SelectedMapPool    { get; init; } = [];
    public string? StartedAt           { get; init; }
    public string? CompletedAt         { get; init; }
    public string? Game                { get; init; } = "valorant";
    public string? Team1LinkToken      { get; init; }
    public string? Team2LinkToken      { get; init; }
}

public sealed record PickedMap(string MapId, string? Side = null);

// ── Veto step sequences ───────────────────────────────────────────────────

public sealed record VetoStep(
    int    ActionNumber,
    string Action,    // ban | pick | pick_side
    string Team,      // T1 | T2
    bool   IsDecider = false);

public sealed record VetoActionHistory(
    Guid    Id,
    Guid    VetoId,
    Guid    MatchId,
    Guid?   TournamentId,
    Guid?   TeamId,
    string? TeamSide,
    string  ActionType,
    string? MapId,
    int     ActionNumber,
    string? Side,
    Guid?   CreatedBy,
    string? CreatedAt);

/// <summary>Enriched veto history row for API responses.</summary>
public sealed record VetoHistoryEntry(
    int     ActionNumber,
    string  TeamSide,
    Guid?   TeamId,
    string? TeamName,
    string  Action,
    string  MapId,
    string? MapName,
    string? MapImageUrl,
    string? Side,
    string? CreatedAt);
