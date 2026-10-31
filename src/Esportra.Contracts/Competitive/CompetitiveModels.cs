namespace Esportra.Contracts.Competitive;

// ── Scrim ───────────────────────────────────────────────────────────────────────
// Non-positional records: Dapper uses property injection, which correctly invokes
// custom TypeHandlers for array columns (int[]?, string[]?) and tolerates queries
// that return different column subsets (RETURNING * vs SELECT * + JOINs).

public sealed record ScrimListingRow
{
    public Guid Id { get; init; }
    public Guid TeamId { get; init; }
    public string? GameSlug { get; init; }
    public string? ModeKey { get; init; }
    public string? RegionKey { get; init; }
    public string? MinRankKey { get; init; }
    public string? MaxRankKey { get; init; }
    public int[]? PreferredDays { get; init; }
    public TimeOnly? PreferredTimeStart { get; init; }
    public TimeOnly? PreferredTimeEnd { get; init; }
    public string? Timezone { get; init; }
    public string? Notes { get; init; }
    public string Status { get; init; } = null!;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string ScheduleType { get; init; } = "one_off";
    public DateTime? ScheduleUntil { get; init; }
    public int? SlotDurationMinutes { get; init; }
    public string? TeamName { get; init; }
    public string? TeamLogoUrl { get; init; }
    public string? TeamTag { get; init; }
    public int? ActiveMemberCount { get; init; }
    public string? MinRankDisplayName { get; init; }
    public string? MinRankIconUrl { get; init; }
    public string? MaxRankDisplayName { get; init; }
    public string? MaxRankIconUrl { get; init; }
    public string? MembersPreview { get; init; }
    public int? ScrimsCompleted { get; init; }
}

public sealed record ScrimOfferRow
{
    public Guid Id { get; init; }
    public Guid ListingId { get; init; }
    public Guid OfferingTeamId { get; init; }
    public DateTime? ProposedAt { get; init; }
    public int? ProposedDurationMinutes { get; init; }
    public string? Notes { get; init; }
    public string Status { get; init; } = null!;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public Guid? SlotId { get; init; }
    public string? OfferingTeamName { get; init; }
    public string? OfferingTeamLogo { get; init; }
    public string? ListingGameSlug { get; init; }
    public string? ListingTeamName { get; init; }
}

public sealed record ScrimSessionRow
{
    public Guid Id { get; init; }
    public Guid Team1Id { get; init; }
    public Guid Team2Id { get; init; }
    public string? GameSlug { get; init; }
    public string? ModeKey { get; init; }
    public DateTime? ScheduledAt { get; init; }
    public int? DurationMinutes { get; init; }
    public string? ServerInfo { get; init; }
    public string? LobbyCode { get; init; }
    public string Status { get; init; } = null!;
    public bool ResultReportedByTeam1 { get; init; }
    public bool ResultReportedByTeam2 { get; init; }
    public int? Team1Score { get; init; }
    public int? Team2Score { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? Team1CheckedInAt { get; init; }
    public DateTime? Team2CheckedInAt { get; init; }
    public string? HangfireJobIds { get; init; }
    public DateTime? CheckinWindowOpenedAt { get; init; }
    public Guid? VetoSessionId { get; init; }
    public string? VetoTeam1Token { get; init; }
    public string? VetoTeam2Token { get; init; }
    public string? Team1Name { get; init; }
    public string? Team1Logo { get; init; }
    public string? Team2Name { get; init; }
    public string? Team2Logo { get; init; }
    public string? VetoMyToken { get; init; }
    public string? MyRole { get; init; }
    public ScrimDisputeRow? Dispute { get; init; }
}

public sealed record ScrimResultReportRow
{
    public Guid Id { get; init; }
    public Guid SessionId { get; init; }
    public Guid ReportingTeamId { get; init; }
    public int Team1Score { get; init; }
    public int Team2Score { get; init; }
    public string? Notes { get; init; }
    public string[]? ScreenshotsUrls { get; init; }
    public DateTime? SubmittedAt { get; init; }
    public string? ReportingTeamName { get; init; }
    public Guid? ConfirmedByTeamId { get; init; }
    public DateTime? ConfirmedAt { get; init; }
}

public sealed record ScrimDisputeRow
{
    public Guid Id { get; init; }
    public Guid SessionId { get; init; }
    public Guid RaisedByTeamId { get; init; }
    public string Reason { get; init; } = null!;
    public string? Status { get; init; }
    public DateTime CreatedAt { get; init; }
    public string[]? EvidenceUrls { get; init; }
    public string[]? ResponderEvidenceUrls { get; init; }
    public string? ResponderNotes { get; init; }
    public Guid? ResolvedBy { get; init; }
    public string? Outcome { get; init; }
    public string? ResolutionNotes { get; init; }
    public DateTime? ResolvedAt { get; init; }
}

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
