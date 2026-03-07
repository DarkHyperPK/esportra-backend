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
