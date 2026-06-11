using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Esportra.Infrastructure.Email;

/// <summary>
/// Sends branded HTML emails via SMTP using MailKit (replaces Resend).
/// Config: Smtp:Host, Smtp:Port, Smtp:Username, Smtp:Password, Smtp:FromEmail, Smtp:FromName
/// </summary>
public sealed class SmtpEmailService(
    IConfiguration config,
    ILogger<SmtpEmailService> logger) : IEmailService
{
    public async Task SendAsync(string toEmail, EmailType type, object data, CancellationToken ct = default)
    {
        var host     = config["Smtp:Host"];
        var username = config["Smtp:Username"];
        var password = config["Smtp:Password"];

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password) || password.StartsWith("REPLACE"))
        {
            logger.LogWarning("[Email] SMTP not configured — skipping {Type} to {Email}", type, toEmail);
            return;
        }

        var port      = int.Parse(config["Smtp:Port"] ?? "465");
        var fromEmail = config["Smtp:FromEmail"] ?? username;
        var fromName  = config["Smtp:FromName"] ?? "Esportra";

        var (subject, html) = BuildTemplate(type, data);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromEmail));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = html };

        logger.LogInformation("[Email] Sending {Type} to {Email} via SMTP", type, toEmail);

        using var client = new SmtpClient();
        try
        {
            // Port 465 = implicit SSL; port 587 = STARTTLS
            var secureSocketOption = port == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

            await client.ConnectAsync(host, port, secureSocketOption, ct);
            await client.AuthenticateAsync(username, password, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            logger.LogInformation("[Email] Sent {Type} to {Email} successfully", type, toEmail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Email] SMTP failed to send {Type} to {Email}", type, toEmail);
        }
    }

    private static (string Subject, string Html) BuildTemplate(EmailType type, object data)
    {
        var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
            System.Text.Json.JsonSerializer.Serialize(data)) ?? [];

        string Get(string key, string fallback = "") =>
            dict.TryGetValue(key, out var v) ? v.GetString() ?? fallback : fallback;

        return type switch
        {
            EmailType.Welcome =>
                EmailTemplates.Welcome(Get("username")),

            EmailType.TournamentRegistration =>
                EmailTemplates.TournamentRegistration(
                    Get("username"), Get("tournamentName"),
                    Get("startDate"), Get("tournamentUrl"),
                    Get("endDate"), Get("game"),
                    Get("teamName"), Get("registrationType")),

            EmailType.TeamInvite =>
                EmailTemplates.TeamInvite(
                    Get("inviteeName"), Get("teamName"),
                    Get("captainName"), Get("acceptUrl")),

            EmailType.StaffInvite =>
                EmailTemplates.StaffInvite(
                    Get("inviteeName"), Get("orgName"),
                    Get("role"), Get("permissions"), Get("acceptUrl")),

            EmailType.PartnerInvite =>
                EmailTemplates.PartnerInvite(Get("sponsorName"), Get("setupUrl")),

            EmailType.PartnerWelcome =>
                EmailTemplates.PartnerWelcome(Get("sponsorName"), Get("portalUrl")),

            EmailType.PasswordReset =>
                EmailTemplates.PasswordReset(Get("resetUrl")),

            EmailType.LicenseApplicationReceived =>
                EmailTemplates.LicenseApplicationReceived(
                    Get("username"), Get("licenseType"), Get("dashboardUrl")),

            EmailType.LicenseApproved =>
                EmailTemplates.LicenseApproved(
                    Get("username"), Get("licenseType"), Get("licenseId"),
                    Get("issuedAt"), Get("expiresAt"), Get("dashboardUrl")),

            EmailType.LicenseRejected =>
                EmailTemplates.LicenseRejected(
                    Get("username"), Get("licenseType"), Get("dashboardUrl")),

            EmailType.TournamentInvite =>
                EmailTemplates.TournamentInvite(
                    Get("captainName"), Get("tournamentName"),
                    Get("code"), Get("tournamentUrl"), Get("expiryDate"),
                    Get("gameHeaderUrl")),

            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown email type")
        };
    }
}
