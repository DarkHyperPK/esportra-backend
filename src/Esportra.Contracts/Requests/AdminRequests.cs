namespace Esportra.Contracts.Requests;

public sealed record ManageUserRequest(string Action, string? Role = null, string? RoleKey = null, string? RoleType = null, string? AssignedBy = null);
// Actions: "delete-user", "update-role", "assign_role", "revoke_role"

public sealed record InviteSponsorRequest(
    string Email,
    string SponsorId);

public sealed record SendEmailRequest(
    string Email,
    string Type,   // EmailType enum name
    object Data);

public sealed record SuspendUserRequest(
    string Reason,
    string? SuspensionType = null,
    DateTime? SuspensionUntil = null);

public sealed record BulkUserActionRequest(Guid[] UserIds, string Action, string? Reason = null);

public sealed record BulkTournamentActionRequest(Guid[] TournamentIds, string Action);

public sealed record RevokeSessionRequest(string? Reason = null);

public sealed record CreateAuditLogRequest(
    string AdminId,
    string AdminName,
    string ActionType,
    string TargetType,
    string TargetId,
    string TargetName,
    string Severity = "low",
    string? UserAgent = null,
    string? IpAddress = null,
    object? Details = null);
