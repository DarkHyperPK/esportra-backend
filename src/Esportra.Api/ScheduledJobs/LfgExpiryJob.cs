using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Esportra.Infrastructure.Email;
using Hangfire;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Expires published LFG listings whose expires_at has passed.
/// Runs every 15 minutes; idempotent — safe to re-run at any time.
/// </summary>
[Queue("default")]
public sealed class LfgExpiryJob(
    IDbConnectionFactory db,
    CompetitiveEmailService emails,
    ILogger<LfgExpiryJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        var expired = (await conn.QueryAsync<(Guid UserId, Guid ListingId)>(
            """
            UPDATE public.lfg_listings
            SET status = 'expired'::lfg_listing_status, updated_at = now()
            WHERE status = 'published' AND expires_at < now()
            RETURNING user_id AS UserId, id AS ListingId
            """)).AsList();

        if (expired.Count == 0)
            return;

        logger.LogInformation("[LfgExpiry] Expired {Count} listing(s).", expired.Count);

        // L5 — email each listing owner that their listing expired (best-effort, one per user)
        var discoveryUrl = $"{emails.FrontendUrl}/competitive/lfg";
        var userIds = expired.Select(e => e.UserId).Distinct().ToArray();
        var usernames = (await conn.QueryAsync<(Guid Id, string? Username)>(
            "SELECT id, username FROM public.profiles WHERE id = ANY(@userIds)",
            new { userIds })).ToDictionary(r => r.Id, r => r.Username);

        foreach (var userId in userIds)
        {
            var username = usernames.GetValueOrDefault(userId) ?? "";
            await emails.SendLfgAsync(userId, EmailType.LfgListingExpired, new { username, discoveryUrl }, ct);
        }
    }
}
