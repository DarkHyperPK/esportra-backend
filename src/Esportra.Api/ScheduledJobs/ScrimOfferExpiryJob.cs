using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Esportra.Infrastructure.Email;
using Hangfire;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Expires scrim offers older than 48 hours.
/// Runs every hour; idempotent — safe to re-run at any time.
/// </summary>
[Queue("default")]
public sealed class ScrimOfferExpiryJob(
    IDbConnectionFactory db,
    CompetitiveEmailService emails,
    ILogger<ScrimOfferExpiryJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        var expired = (await conn.QueryAsync<ExpiredOffer>(
            """
            WITH expired AS (
                UPDATE public.scrim_offers
                SET status = 'expired'::scrim_offer_status, updated_at = now()
                WHERE status = 'pending'
                  AND created_at < now() - INTERVAL '48 hours'
                RETURNING id, offering_team_id, listing_id
            )
            SELECT e.id AS Id, e.offering_team_id AS OfferingTeamId, l.team_id AS ListingTeamId
            FROM expired e
            LEFT JOIN public.scrim_listings l ON l.id = e.listing_id
            """)).AsList();

        if (expired.Count == 0)
            return;

        logger.LogInformation("[ScrimOfferExpiry] Expired {Count} offer(s).", expired.Count);

        // S5 — email each offering team's staff that their scrim offer expired (best-effort)
        var discoveryUrl = $"{emails.FrontendUrl}/competitive/scrims";
        foreach (var offer in expired)
        {
            var listingTeamName = offer.ListingTeamId.HasValue
                ? await conn.ExecuteScalarAsync<string?>(
                    "SELECT name FROM public.teams WHERE id = @id", new { id = offer.ListingTeamId }) ?? "the listing team"
                : "the listing team";
            var staffIds = await CompetitiveEmailService.GetTeamStaffIdsAsync(conn, offer.OfferingTeamId);
            foreach (var staffId in staffIds)
                await emails.SendScrimAsync(staffId, EmailType.ScrimOfferExpired, new { listingTeamName, discoveryUrl }, ct);
        }
    }

    private sealed record ExpiredOffer(Guid Id, Guid OfferingTeamId, Guid? ListingTeamId);
}
