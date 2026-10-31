namespace Esportra.Contracts.Competitive;

// ── Scrim ───────────────────────────────────────────────────────────────────────

public sealed record ScrimListingRow(
    Guid Id, Guid TeamId, string? GameSlug, string? ModeKey, string? RegionKey,
    string? MinRankKey, string? MaxRankKey, int[]? PreferredDays,
    TimeOnly? PreferredTimeStart, TimeOnly? PreferredTimeEnd,
    string? Timezone, string? Notes, string Status,
    DateTime CreatedAt, DateTime UpdatedAt,
    string ScheduleType = "one_off",
    DateTime? ScheduleUntil = null,
    int? SlotDurationMinutes = null,
    string? TeamName = null, string? TeamLogoUrl = null, string? TeamTag = null);

public sealed record ScrimOfferRow(
    Guid Id, Guid ListingId, Guid OfferingTeamId,
    DateTime? ProposedAt, int? ProposedDurationMinutes,
    string? Notes, string Status, DateTime CreatedAt, DateTime UpdatedAt,
    string? OfferingTeamName = null, string? OfferingTeamLogo = null,
    string? ListingGameSlug = null, string? ListingTeamName = null,
    Guid? SlotId = null);

public sealed record ScrimSessionRow(
    Guid Id, Guid Team1Id, Guid Team2Id, string? GameSlug,
    DateTime? ScheduledAt, int? DurationMinutes, string Status,
    bool ResultReportedByTeam1, bool ResultReportedByTeam2,
    int? Team1Score, int? Team2Score, DateTime CreatedAt, DateTime UpdatedAt,
    string? ModeKey = null, string? ServerInfo = null, string? LobbyCode = null,
    string? Team1Name = null, string? Team1Logo = null,
    string? Team2Name = null, string? Team2Logo = null,
    DateTime? Team1CheckedInAt = null,
    DateTime? Team2CheckedInAt = null,
    DateTime? CheckinWindowOpenedAt = null,
    Guid? VetoSessionId = null,
    // VetoMyToken is NOT stored — derived per caller in GetSessionAsync.
    // Raw veto_team1_token / veto_team2_token columns are intentionally excluded.
    string? VetoMyToken = null,
    // MyRole is the caller's team_members.role ('captain' or 'manager') — set by GetSessionAsync only.
    string? MyRole = null,
    // Dispute is populated by GetSessionAsync only — not returned by other endpoints.
    ScrimDisputeRow? Dispute = null);

public sealed record ScrimResultReportRow(
    Guid Id, Guid SessionId, Guid ReportingTeamId,
    int Team1Score, int Team2Score, string? Notes,
    string[]? ScreenshotsUrls, DateTime? SubmittedAt,
    string? ReportingTeamName = null,
    Guid? ConfirmedByTeamId = null,
    DateTime? ConfirmedAt = null);

public sealed record ScrimDisputeRow(
    Guid Id, Guid SessionId, Guid RaisedByTeamId,
    string Reason, DateTime CreatedAt, string? Status = null,
    string[]? EvidenceUrls = null,
    string[]? ResponderEvidenceUrls = null,
    string? ResponderNotes = null,
    Guid? ResolvedBy = null,
    string? Outcome = null,
    string? ResolutionNotes = null,
    DateTime? ResolvedAt = null);

public sealed record ScrimSlotRow(
    Guid Id, Guid ListingId,
    DateTime StartsAt, DateTime EndsAt,
    string Status, DateTime CreatedAt,
    Guid? BookedByTeamId = null,
    Guid? SessionId = null);

// ── Practice ────────────────────────────────────────────────────────────────────

public sealed record PracticeSessionRow(
    Guid Id, Guid TeamId, string? GameSlug, string Title,
    DateTime? ScheduledAt, int? DurationMinutes,
    string? Notes, string Status, Guid CreatedBy,
    DateTime CreatedAt, DateTime UpdatedAt,
    string? TeamName = null);

public sealed record PracticeAttendanceRow(
    Guid Id, Guid SessionId, Guid UserId, string Status,
    string? Username = null, string? AvatarSeed = null);

// ── Recruitment ─────────────────────────────────────────────────────────────────

public sealed record RecruitmentOpeningRow
{
    public Guid Id { get; init; }
    public Guid TeamId { get; init; }
    public string GameSlug { get; init; } = null!;
    public string Title { get; init; } = null!;
    public string? Description { get; init; }
    public string[]? Requirements { get; init; }
    public int? PositionsAvailable { get; init; }
    public int PositionsFilled { get; init; }
    public string Mode { get; init; } = null!;
    public Guid? TournamentId { get; init; }
    public string Status { get; init; } = null!;
    public Guid CreatedBy { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string? TeamName { get; init; }
    public string? TeamLogoUrl { get; init; }
    public string? TeamTag { get; init; }
}

public sealed record RecruitmentApplicationRow(
    Guid Id, Guid OpeningId, Guid ApplicantUserId, string Status,
    string? CoverLetter, DateTime CreatedAt, DateTime UpdatedAt,
    string? ApplicantUsername = null, string? ApplicantAvatarSeed = null,
    string? OpeningTitle = null, string? GameSlug = null,
    string? TeamName = null, string? TeamLogoUrl = null);

// ── Tryouts ─────────────────────────────────────────────────────────────────────

public sealed record TryoutSessionRow(
    Guid Id, Guid TeamId, string? GameSlug, string Title,
    string? Description, DateTime? ScheduledAt,
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
    string Title, string? GameSlug, DateTime? ScheduledAt,
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
    public bool IsSaved { get; init; }
    public Guid? ViewerInterestId { get; init; }
    public string? ViewerInterestStatus { get; init; }
    public Guid? ViewerConversationId { get; init; }
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
    public string[]? PreferredAgents { get; init; }
    public string[]? Languages { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string Status { get; init; } = null!;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public Guid UserId { get; init; }
    public string? Username { get; init; }
    public string? AvatarSeed { get; init; }
    public string? ProfileAvatarUrl { get; init; }
    public string? ProfileBannerUrl { get; init; }
    public string? AvatarStyle { get; init; }
    public string? RegionDisplayName { get; init; }
    public string? MinRankDisplayName { get; init; }
    public string? MinRankIconUrl { get; init; }
    public string? MaxRankDisplayName { get; init; }
    public string? MaxRankIconUrl { get; init; }
    // Riot stats enrichment
    public string? PlayerCardId { get; init; }
    public int? CompetitiveTier { get; init; }
    public string? CompetitiveTierName { get; init; }
    public int? WinCount { get; init; }
    public int? LossCount { get; init; }
    public decimal? WinRate { get; init; }
    public decimal? KdRatio { get; init; }
    public decimal? HsPct { get; init; }
    public string? PopularAgents { get; init; }
    public string? StatsSyncStatus { get; init; }
    public int? MatchesAnalyzed { get; init; }
    public DateTimeOffset? RiotSyncedAt { get; init; }
    // Riot identity
    public string? RiotGameName { get; init; }
    public string? RiotTagLine { get; init; }
    // Team enrichment (for LFP listings)
    public string? TeamName { get; init; }
    public string? TeamLogoUrl { get; init; }
    public string? TeamCaptainPlayerCardId { get; init; }
    public string? TeamTag { get; init; }
    public int? TeamMemberCount { get; init; }
    public int? TeamVerifiedCount { get; init; }
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
    public string? ProfileAvatarUrl { get; init; }
    public string? AvatarStyle { get; init; }
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
    Guid Id, string GameSlug, string RegionKey, string DisplayName, string[]? ServerIds = null);

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
