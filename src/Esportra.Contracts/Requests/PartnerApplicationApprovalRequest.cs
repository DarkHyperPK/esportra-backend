namespace Esportra.Contracts.Requests;

public sealed record PartnerApplicationApprovalRequest(
    string? InvitationEmail = null,
    string? Tier = null);
