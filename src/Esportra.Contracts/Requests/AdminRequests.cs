namespace Esportra.Contracts.Requests;

public sealed record ManageUserRequest(string Action, string? Role = null);
// Actions: "delete-user", "update-role"

public sealed record InviteSponsorRequest(
    string Email,
    string SponsorId,
    string? ApplicationId = null);

public sealed record SendEmailRequest(
    string Email,
    string Type,   // EmailType enum name
    object Data);

public sealed record SuspendUserRequest(string Reason);

public sealed record SponsorTrackRequest(
    string  SponsorId,
    string  EventType,
    string? PageUrl = null);

public sealed record CreateAuditLogRequest(
    string  AdminId,
    string  AdminName,
    string  ActionType,
    string  TargetType,
    string  TargetId,
    string  TargetName,
    string  Severity = "low",
    string? UserAgent = null,
    string? IpAddress = null,
    object? Details   = null);
