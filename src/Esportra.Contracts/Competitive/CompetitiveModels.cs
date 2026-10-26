namespace Esportra.Contracts.Competitive;

// ── Scrim ───────────────────────────────────────────────────────────────────────

public sealed record ScrimListingRow(
    Guid Id, Guid TeamId, string? GameSlug, string? ModeKey, string? RegionKey,
    string? MinRankKey, string? MaxRankKey, int[]? PreferredDays,
    TimeOnly? PreferredTimeStart, TimeOnly? PreferredTimeEnd,
    string? Timezone, string? Notes, string Status,
    DateTime CreatedAt, DateTime UpdatedAt,
    string? TeamName = null, string? TeamLogoUrl = null, string? TeamTag = null);

public sealed record ScrimOfferRow(
    Guid Id, Guid ListingId, Guid OfferingTeamId,
    DateTimeOffset? ProposedAt, int? ProposedDurationMinutes,
    string? Notes, string Status, DateTime CreatedAt, DateTime UpdatedAt,
    string? OfferingTeamName = null, string? OfferingTeamLogo = null,
    string? ListingGameSlug = null, string? ListingTeamName = null);

public sealed record ScrimSessionRow(
    Guid Id, Guid Team1Id, Guid Team2Id, string? GameSlug,
    DateTimeOffset? ScheduledAt, int? DurationMinutes, string Status,
    bool ResultReportedByTeam1, bool ResultReportedByTeam2,
    int? Team1Score, int? Team2Score, DateTime CreatedAt, DateTime UpdatedAt,
    string? ModeKey = null, string? ServerInfo = null, string? LobbyCode = null,
    string? Team1Name = null, string? Team1Logo = null,
    string? Team2Name = null, string? Team2Logo = null);

public sealed record ScrimResultReportRow(
    Guid Id, Guid SessionId, Guid ReportingTeamId,
    int Team1Score, int Team2Score, string? Notes,
    string[]? ScreenshotsUrls, DateTime? SubmittedAt,
    string? ReportingTeamName = null);

public sealed record ScrimDisputeRow(
    Guid Id, Guid SessionId, Guid RaisedByTeamId,
    string Reason, DateTime CreatedAt, string? Status = null);

// ── Practice ────────────────────────────────────────────────────────────────────

public sealed record PracticeSessionRow(
    Guid Id, Guid TeamId, string? GameSlug, string Title,
    DateTimeOffset? ScheduledAt, int? DurationMinutes,
    string? Notes, string Status, Guid CreatedBy,
    DateTime CreatedAt, DateTime UpdatedAt,
    string? TeamName = null);

public sealed record PracticeAttendanceRow(
    Guid Id, Guid SessionId, Guid UserId, string Status,
    string? Username = null, string? AvatarSeed = null);

// ── Recruitment ─────────────────────────────────────────────────────────────────

public sealed record RecruitmentOpeningRow(
    Guid Id, Guid TeamId, string GameSlug, string Title,
    string? Description, string[]? Requirements,
    int? PositionsAvailable, int PositionsFilled,
    string Mode, Guid? TournamentId, string Status,
    Guid CreatedBy, DateTimeOffset? ExpiresAt,
    DateTime CreatedAt, DateTime UpdatedAt,
    string? TeamName = null, string? TeamLogoUrl = null, string? TeamTag = null);

public sealed record RecruitmentApplicationRow(
    Guid Id, Guid OpeningId, Guid ApplicantUserId, string Status,
    string? CoverLetter, DateTime CreatedAt, DateTime UpdatedAt,
    string? ApplicantUsername = null, string? ApplicantAvatarSeed = null,
    string? OpeningTitle = null, string? GameSlug = null,
    string? TeamName = null, string? TeamLogoUrl = null);

// ── Tryouts ─────────────────────────────────────────────────────────────────────

public sealed record TryoutSessionRow(
    Guid Id, Guid TeamId, string? GameSlug, string Title,
    string? Description, DateTimeOffset? ScheduledAt,
    int? DurationMinutes, int? MaxParticipants,
    string? MinRankKey, string? MaxRankKey,
    string Status, Guid CreatedBy, DateTime CreatedAt, DateTime UpdatedAt,
    string? TeamName = null, string? TeamLogoUrl = null,
    long? ParticipantCount = null);

public sealed record TryoutParticipantRow(
    Guid Id, Guid SessionId, Guid UserId, Guid InvitedBy, string Status,
    DateTime InvitedAt, DateTime? RespondedAt,
    string? Username = null, string? AvatarSeed = null);

public sealed record TryoutEvaluationRow(
    Guid Id, Guid SessionId, Guid EvaluateeUserId, Guid EvaluatorUserId,
    short OverallScore, short Communication, short Mechanics, short GameSense,
    short Attitude, short Teamwork, short PeakPlay, short Consistency,
    string? Notes, string Recommendation, DateTime CreatedAt, DateTime UpdatedAt,
    string? EvaluateeUsername = null, string? EvaluateeAvatarSeed = null,
    string? EvaluatorUsername = null);

public sealed record TryoutInviteRow(
    Guid SessionId, Guid ParticipantId, string ParticipantStatus,
    string Title, string? GameSlug, DateTimeOffset? ScheduledAt,
    string? TeamName, string? TeamLogoUrl, string SessionStatus);

// ── LFG ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Non-positional record so Dapper uses property injection, which invokes
/// custom TypeHandlers (StringArrayTypeHandler) for string[]? columns.
/// Constructor injection uses GetFieldType() which returns typeof(Array) for
/// Npgsql text[] and cannot match string[]? constructor parameters.
/// </summary>
public sealed record LfgListingRow
{
    public Guid Id { get; init; }
    public Guid? TeamId { get; init; }
    public string GameSlug { get; init; } = null!;
    public string Intent { get; init; } = null!;
    public string? ModeKey { get; init; }
    public string? RegionKey { get; init; }
    public string[]? PreferredServers { get; init; }
    public int? MinRankSortOrder { get; init; }
    public int? MaxRankSortOrder { get; init; }
    public string[]? PreferredRoles { get; init; }
    public string[]? Languages { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string Status { get; init; } = null!;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public Guid UserId { get; init; }
    public string? Username { get; init; }
    public string? AvatarSeed { get; init; }
    public string? RegionDisplayName { get; init; }
    public string? MinRankDisplayName { get; init; }
    public string? MinRankIconUrl { get; init; }
    public string? MaxRankDisplayName { get; init; }
    public string? MaxRankIconUrl { get; init; }
}

public sealed record LfgInterestRow(
    Guid Id, Guid ListingId, Guid FromUserId, Guid ToUserId,
    string Status, string? Message, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>
/// Enriched interests row for GET /api/competitive/lfg/{id}/interests.
/// Joins profiles, player_game_profiles, game_catalog_rank_tiers, game_catalog_regions.
/// Non-positional record so Dapper uses property injection for string[]? PreferredRoles.
/// </summary>
public sealed record LfgInterestEnrichedRow
{
    public Guid Id { get; init; }
    public Guid ListingId { get; init; }
    public Guid FromUserId { get; init; }
    public string Status { get; init; } = null!;
    public string? Message { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? Username { get; init; }
    public string? AvatarSeed { get; init; }
    public string? RankTierKey { get; init; }
    public string? RankSource { get; init; }
    public string[]? PreferredRoles { get; init; }
    public string? RegionKey { get; init; }
    public string? RankTierDisplayName { get; init; }
    public string? RankTierIconUrl { get; init; }
    public string? RegionDisplayName { get; init; }
}

// ── Catalog ───────────────────────────────────────────────────────────────────────

public sealed record CatalogRankTierRow(
    Guid Id, string GameSlug, string TierKey, string DisplayName, int SortOrder, int DivisionCount,
    string? IconUrl = null);

public sealed record CatalogGameRoleRow(
    Guid Id, string GameSlug, string RoleKey, string DisplayName, int SortOrder,
    string? IconUrl = null);

public sealed record CatalogRegionRow(
    Guid Id, string GameSlug, string RegionKey, string DisplayName);

// ── Scouting ─────────────────────────────────────────────────────────────────────

public sealed record ScoutingShortlistRow(
    Guid Id, Guid OwnerUserId, string Name, string? Description,
    string? GameSlug, bool IsPrivate, DateTime CreatedAt, DateTime UpdatedAt,
    long? EntryCount = null);

public sealed record ShortlistEntryRow(
    Guid Id, Guid PlayerUserId, string? Note, DateTime AddedAt,
    Guid ShortlistId = default,
    string? PlayerUsername = null, string? PlayerAvatarSeed = null);

public sealed record ScoutingNoteRow(
    Guid Id, Guid ScoutUserId, Guid SubjectUserId,
    string? GameSlug, string Content, DateTime CreatedAt, DateTime UpdatedAt);
