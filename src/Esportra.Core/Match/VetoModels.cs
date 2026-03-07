namespace Esportra.Core.Match;

// ── Veto State Machine Types ───────────────────────────────────────────────

public enum VetoState { Init, Ban, Pick, PickSide, Complete }

public enum VetoEvent { SetBo, BanMap, PickMap, PickSide, Reset }

public sealed record VetoTransitionResult(bool Ok, string? Reason = null);

public sealed record TurnContext(
    string? CurrentTeamId,
    bool    IsOrganizer,
    string? UserTeamId,
    bool    IsCaptain);

// ── DB row model (matches match_map_veto table) ───────────────────────────

public sealed record MatchMapVeto
{
    public string  Id                  { get; init; } = "";
    public string  MatchId             { get; init; } = "";
    public string  TournamentId        { get; init; } = "";
    public string? Team1Id             { get; init; }
    public string? Team2Id             { get; init; }
    public int     BestOf              { get; init; } = 1;
    public string  Status              { get; init; } = "pending"; // pending | in_progress | completed | cancelled
    public string? CurrentTeamId       { get; init; }
    public string? CurrentAction       { get; init; }              // ban | pick | pick_side
    public int     CurrentActionNumber { get; init; }
    public string[]  Team1BannedMaps   { get; init; } = [];
    public string[]  Team2BannedMaps   { get; init; } = [];
    public PickedMap[] Team1PickedMaps { get; init; } = [];
    public PickedMap[] Team2PickedMaps { get; init; } = [];
    public string? SelectedMapId       { get; init; }
    public string? StartedAt           { get; init; }
    public string? CompletedAt         { get; init; }
    public string? Game                { get; init; } = "valorant";
}

public sealed record PickedMap(string MapId, string? Side = null);

// ── Veto step sequences ───────────────────────────────────────────────────

public sealed record VetoStep(
    int    ActionNumber,
    string Action,    // ban | pick | pick_side
    string Team,      // T1 | T2
    bool   IsDecider = false);
