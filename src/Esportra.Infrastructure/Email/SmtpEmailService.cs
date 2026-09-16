using System.Text.Json;
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
        var host = config["Smtp:Host"];
        var username = config["Smtp:Username"];
        var password = config["Smtp:Password"];

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password) || password.StartsWith("REPLACE"))
        {
            throw new InvalidOperationException("SMTP email service is not configured.");
        }

        var port = int.Parse(config["Smtp:Port"] ?? "465");
        var fromEmail = config["Smtp:FromEmail"] ?? username;
        var fromName = config["Smtp:FromName"] ?? "Esportra";

        var (subject, html) = BuildTemplate(type, data);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromEmail));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = html };

        logger.LogInformation("[Email] Sending {Type} via SMTP", type);

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

            logger.LogInformation("[Email] Sent {Type} successfully", type);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Email] SMTP failed to send {Type}", type);
            throw;
        }
    }

    private static (string Subject, string Html) BuildTemplate(EmailType type, object data)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(data)) ?? [];

        string Get(string key, string fallback = "") =>
            dict.TryGetValue(key, out var v)
                ? v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString() ?? fallback,
                    JsonValueKind.Number => v.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => fallback
                }
                : fallback;

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
                EmailTemplates.PartnerInvite(
                    Get("sponsorName"),
                    Get("invitationUrl"),
                    bool.TryParse(Get("accountExists"), out var accountExists) && accountExists,
                    bool.TryParse(Get("requiresPasswordSetup"), out var requiresPasswordSetup) && requiresPasswordSetup),

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

            EmailType.Broadcast =>
                EmailTemplates.Broadcast(
                    Get("title"), Get("content"),
                    Get("broadcastType", "announcement"), Get("priority", "normal")),

            EmailType.MatchChatMessage =>
                EmailTemplates.MatchChatMessage(
                    Get("senderTeamName"),
                    Get("messagePreview"),
                    Get("matchRoomUrl"),
                    int.TryParse(Get("unreadCount"), out var uc) ? uc : 1),

            EmailType.DisputeResolved =>
                EmailTemplates.DisputeResolved(
                    Get("referenceNumber"), Get("title"), Get("status"),
                    Get("resolutionNotes"), Get("tournamentName"),
                    Get("disputeUrl"), Get("recipientType"), Get("filerName")),

            EmailType.DisputeComment =>
                EmailTemplates.DisputeComment(
                    Get("referenceNumber"), Get("commenterName"),
                    Get("commentPreview"), Get("disputeUrl"),
                    Get("tournamentName")),

            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown email type")
        };
    }
}
