namespace Esportra.Infrastructure.Email;

public interface IEmailService
{
    Task SendAsync(string toEmail, EmailType type, object data, CancellationToken ct = default);
}

public enum EmailType
{
    Welcome,
    TournamentRegistration,
    TeamInvite,
    StaffInvite,
    PartnerWelcome,
    PartnerInvite,
    PasswordReset,
    LicenseApplicationReceived,
    LicenseApproved,
    LicenseRejected,
    TournamentInvite,
    Broadcast,
    MatchChatMessage,
    DisputeResolved,
    DisputeComment,
    BroadcastCrewInvite,
    // LFG
    LfgInterestReceived,
    LfgInterestAccepted,
    LfgInterestDeclined,
    LfgListingExpiring,
    LfgListingExpired,
    LfgSwipeMatch,
    // Scrims
    ScrimOfferReceived,
    ScrimOfferAccepted,
    ScrimOfferDeclined,
    ScrimOfferWithdrawn,
    ScrimOfferExpired,
    ScrimSessionCancelled,
    ScrimReminder,
    ScrimCheckinOpen,
    ScrimTimeoutCancelled,
    ScrimTimeProposed,
    ScrimTimeClearRequested,
    ScrimTimeAccepted,
    ScrimTimeReset,
    ScrimResultSubmitted,
    ScrimDisputeRaised,
    ScrimDisputeResolved,
}
