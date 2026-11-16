using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Esportra.Infrastructure.Email;
using Hangfire;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Hourly sweep: finds published LFG listings expiring in the next 23–25 hours
/// and sends the owner a heads-up email. Uses expiry_email_sent_at as an
/// idempotency guard to prevent duplicate sends across sweep runs.
/// </summary>
[Queue("notifications")]
public sealed class LfgListingExpiringJob(
    IDbConnectionFactory db,
    CompetitiveEmailService emails,
    ILogger<LfgListingExpiringJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        var rows = (await conn.QueryAsync<ExpiringListing>(
            """
            SELECT l.id, l.user_id AS UserId, l.expires_at AS ExpiresAt, p.username AS Username
            FROM public.lfg_listings l
            JOIN public.profiles p ON p.id = l.user_id
            WHERE l.status = 'published'
              AND l.expiry_email_sent_at IS NULL
              AND l.expires_at BETWEEN NOW() + INTERVAL '23 hours'
                                   AND NOW() + INTERVAL '25 hours'
            """)).AsList();

        if (rows.Count == 0)
            return;

        var frontendUrl = emails.FrontendUrl;

        foreach (var row in rows)
        {
            var expiresAtStr = row.ExpiresAt.ToString("ddd d MMM yyyy, HH:mm 'UTC'");
            var listingUrl = $"{frontendUrl}/competitive/lfg/{row.Id}";

            await emails.SendLfgAsync(row.UserId, EmailType.LfgListingExpiring, new
            {
                username = row.Username ?? "",
                listingUrl,
                expiresAt = expiresAtStr,
            }, ct);

            await conn.ExecuteAsync(
                "UPDATE public.lfg_listings SET expiry_email_sent_at = NOW() WHERE id = @id",
                new { row.Id });
        }

        logger.LogInformation("[LfgListingExpiring] Sent expiry warnings for {Count} listing(s)", rows.Count);
    }

    private sealed record ExpiringListing(Guid Id, Guid UserId, DateTime ExpiresAt, string? Username);
}
