namespace Esportra.Contracts.Requests;

public sealed record CreatePartnerSponsorInvitationRequest(
    string Email,
    string Role = "owner");

public sealed record AcceptPartnerSponsorInvitationRequest(string Token);

public sealed record PreviewPartnerSponsorInvitationRequest(string Token);