using Esportra.Infrastructure.Email;
using Xunit;

namespace Esportra.Api.Tests;

/// <summary>
/// Renders every email template to artifacts/email-previews/ for visual review.
/// No-op unless ESPORTRA_EMAIL_PREVIEWS=1 — keeps CI side-effect free.
/// Run: ESPORTRA_EMAIL_PREVIEWS=1 dotnet test --filter EmailTemplatePreviewTests
/// </summary>
public sealed class EmailTemplatePreviewTests
{
    private static string RepoRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string PreviewDir() => Path.Combine(RepoRoot(), "artifacts", "email-previews");

    [Fact]
    public void RenderPreviewPack()
    {
        if (Environment.GetEnvironmentVariable("ESPORTRA_EMAIL_PREVIEWS") != "1") return;

        EmailTemplates.Init("https://esportra.com", "https://api.esportra.com");
        var dir = PreviewDir();
        Directory.CreateDirectory(dir);

        void Save(string name, string html) =>
            File.WriteAllText(Path.Combine(dir, name + ".html"), html);

        var (regSubject, regHtml) = EmailTemplates.TournamentRegistration(
            "PlayerOne", "Winter Invitational 2026", "Sat, Feb 14 — 18:00 UTC",
            $"{EmailTemplatesInit.FrontendUrl}/tournaments/winter-invitational",
            endDate: "Sun, Feb 15 — 22:00 UTC", game: "Counter-Strike 2",
            teamName: "Team Liquid", registrationType: "Team");
        _ = regSubject;
        Save("01-tournament-registration", regHtml);

        Save("02-team-invite", EmailTemplates.TeamInvite(
            "PlayerTwo", "Team Liquid", "PlayerOne",
            "https://esportra.com/invites/accept?token=demo").Html);

        Save("03-staff-invite", EmailTemplates.StaffInvite(
            "ModUser", "Nexus Esports", "Moderator", "Manage matches · Resolve disputes",
            "https://esportra.com/staff/accept?token=demo").Html);

        Save("04-tournament-invite", EmailTemplates.TournamentInvite(
            "CaptainOne", "Winter Invitational 2026", "WINTER-X42",
            "https://esportra.com/tournaments/winter-invitational", "Feb 10, 2026").Html);

        var (partnerSub, partnerHtml) = EmailTemplates.PartnerInvite(
            "HyperX", "https://partner.esportra.com/invite/accept?t=demo",
            accountExists: false, requiresPasswordSetup: true);
        _ = partnerSub;
        Save("05-partner-invite", partnerHtml);

        Save("06-partner-welcome", EmailTemplates.PartnerWelcome(
            "HyperX", "https://partner.esportra.com").Html);

        Save("07-password-reset", EmailTemplates.PasswordReset(
            "https://esportra.com/auth/reset-password?t=demo").Html);

        Save("08-license-received", EmailTemplates.LicenseApplicationReceived(
            "OrgOwner", "organizer", "https://esportra.com/account/verification-status").Html);

        Save("09-license-approved", EmailTemplates.LicenseApproved(
            "OrgOwner", "organizer", "LIC-2026-0001",
            DateTime.UtcNow.ToString("MMM dd, yyyy"),
            DateTime.UtcNow.AddYears(1).ToString("MMM dd, yyyy"),
            "https://esportra.com/account/verification-status").Html);

        Save("10-license-rejected", EmailTemplates.LicenseRejected(
            "OrgOwner", "venue_owner", "https://esportra.com/account/verification-status").Html);

        Save("11-broadcast-announcement", EmailTemplates.Broadcast(
            "Season 4 kicks off this Friday", "Registration is open for the opening bracket.", "tournament").Html);

        Save("12-broadcast-maintenance", EmailTemplates.Broadcast(
            "Scheduled maintenance Saturday 02:00 UTC", "The platform will be read-only for about 30 minutes.", "maintenance", "high").Html);

        // Auth templates are static files — copy them alongside for one-stop review.
        var gotrueDir = Path.Combine(RepoRoot(), "gotrue-templates");
        foreach (var f in Directory.GetFiles(gotrueDir, "*.html"))
            File.Copy(f, Path.Combine(dir, "auth-" + Path.GetFileName(f)), overwrite: true);
    }
}

/// <summary>Exposes Init-configured URLs to the preview renderer without touching prod state.</summary>
internal static class EmailTemplatesInit
{
    public static string FrontendUrl => "https://esportra.com";
}
