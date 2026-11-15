using Esportra.Api.Services;
using Esportra.Infrastructure.Email;
using Hangfire;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Generic Hangfire job for competitive (LFG/Scrim) email delivery.
/// HTTP endpoints enqueue this instead of calling IEmailService directly,
/// so email never delays the HTTP response.
/// </summary>
[Queue("notifications")]
public sealed class CompetitiveEmailJob(CompetitiveEmailService emails)
{
    public Task RunAsync(Guid userId, string prefKey, string emailType, string dataJson, CancellationToken ct)
    {
        var type = Enum.Parse<EmailType>(emailType);
        var data = System.Text.Json.JsonSerializer.Deserialize<object>(dataJson) ?? new { };

        return prefKey == "email_lfg"
            ? emails.SendLfgAsync(userId, type, data, ct)
            : emails.SendScrimAsync(userId, type, data, ct);
    }
}
