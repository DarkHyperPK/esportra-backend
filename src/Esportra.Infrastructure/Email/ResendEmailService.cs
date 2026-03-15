using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Esportra.Infrastructure.Email;

/// <summary>
/// Sends emails via the Resend API. Ported from supabase/functions/send-email/index.ts.
/// Config required: Resend:ApiKey, Resend:FromEmail, Resend:FromName
/// </summary>
public sealed class ResendEmailService(
    HttpClient http,
    IConfiguration config,
    ILogger<ResendEmailService> logger) : IEmailService
{
    private readonly string _from = $"{config["Resend:FromName"] ?? "Esportra"} <{config["Resend:FromEmail"] ?? "noreply@esportra.com"}>";

    public async Task SendAsync(string toEmail, EmailType type, object data, CancellationToken ct = default)
    {
        var apiKey = config["Resend:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("REPLACE"))
        {
            logger.LogWarning("[Email] Resend API key not configured — skipping {Type} to {Email}", type, toEmail);
            return;
        }

        var (subject, html) = BuildTemplate(type, data);

        var payload = new
        {
            from    = _from,
            to      = new[] { toEmail },
            subject = subject,
            html    = html,
        };

        logger.LogInformation("[Email] Sending {Type} to {Email} (from: {From})", type, toEmail, _from);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = JsonContent.Create(payload);

            var response = await http.SendAsync(request, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(cts.Token);
                logger.LogError("[Email] Resend API error {Status}: {Body}", response.StatusCode, err);
            }
            else
            {
                logger.LogInformation("[Email] Sent {Type} email to {Email} successfully", type, toEmail);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("[Email] Resend API timed out for {Type} to {Email}", type, toEmail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Email] Failed to send {Type} to {Email}", type, toEmail);
        }
    }

    private static (string Subject, string Html) BuildTemplate(EmailType type, object data)
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(data)) ?? [];

        string Get(string key, string fallback = "") =>
            d.TryGetValue(key, out var v) ? v.GetString() ?? fallback : fallback;

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

            EmailType.CheckinReminder or EmailType.MatchCheckinReminder =>
                EmailTemplates.CheckinReminder(
                    Get("username"), Get("tournamentName"), Get("checkInUrl")),

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

            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown email type")
        };
    }
}
