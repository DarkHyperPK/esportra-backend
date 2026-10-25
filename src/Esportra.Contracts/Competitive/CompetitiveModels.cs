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

public sealed record LfgListingRow(
    Guid Id, string GameSlug, string Intent,
    string? ModeKey, string? RegionKey,
    string[]? PreferredServers, int? MinRankSortOrder, int? MaxRankSortOrder,
    string[]? PreferredRoles, string[]? Languages,
    string? Description, DateTimeOffset? ExpiresAt, string Status,
    DateTime CreatedAt, DateTime UpdatedAt,
    Guid UserId = default,
    string? Username = null, string? AvatarSeed = null,
    string? RegionDisplayName = null,
    string? MinRankDisplayName = null,
    string? MinRankIconUrl = null,
    string? MaxRankDisplayName = null,
    string? MaxRankIconUrl = null);

public sealed record LfgInterestRow(
    Guid Id, Guid ListingId, Guid FromUserId, Guid ToUserId,
    string Status, string? Message, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>
/// Enriched interests row for GET /api/competitive/lfg/{id}/interests.
/// Joins profiles, player_game_profiles, game_catalog_rank_tiers, game_catalog_regions.
/// </summary>
public sealed record LfgInterestEnrichedRow(
    Guid Id, Guid ListingId, Guid FromUserId, string Status, string? Message, DateTime CreatedAt,
    string? Username = null, string? AvatarSeed = null,
    string? RankTierKey = null, string? RankSource = null,
    string[]? PreferredRoles = null, string? RegionKey = null,
    string? RankTierDisplayName = null, string? RankTierIconUrl = null,
    string? RegionDisplayName = null);

// ── Catalog ───────────────────────────────────────────────────────────────────────

public sealed record CatalogRankTierRow(
    Guid Id, string GameSlug, string TierKey, string DisplayName, int SortOrder, int DivisionCount,
    string? IconUrl = null);

public sealed record CatalogGameRoleRow(
    Guid Id, string GameSlug, string RoleKey, string DisplayName, int SortOrder,
    string? IconUrl = null);

public sealed record CatalogRegionRow(
    Guid Id, string GameSlug, string RegionKey, string DisplayName,
    string[]? ServerIds = null);

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
