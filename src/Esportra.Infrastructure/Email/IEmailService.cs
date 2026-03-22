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
}
