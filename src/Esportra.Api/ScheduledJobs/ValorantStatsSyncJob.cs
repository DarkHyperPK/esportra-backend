using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Hangfire;

namespace Esportra.Api.ScheduledJobs;

[Queue("default")]
public sealed class ValorantStatsSyncJob(
    IDbConnectionFactory db,
    ValorantSyncRequestService syncRequests,
    ILogger<ValorantStatsSyncJob> logger)
{
    private const int BatchSize = 20;

    public async Task ExecuteAsync(CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        // Apply the limit after selecting stale accounts. Limiting by user id first
        // permanently starves accounts outside the first page of eligible users.
        var stale = (await conn.QueryAsync<Guid>(
            """
            SELECT ra.user_id
            FROM public.riot_accounts ra
            LEFT JOIN public.player_game_stats pgs
                ON pgs.user_id = ra.user_id AND pgs.game_slug = 'valorant'
            WHERE (EXISTS (
                SELECT 1 FROM public.lfg_listings l
                WHERE l.user_id = ra.user_id AND l.game_slug = 'valorant' AND l.status = 'published'
            ) OR EXISTS (
                SELECT 1 FROM public.player_game_profiles pgp
                WHERE pgp.user_id = ra.user_id AND pgp.game_slug = 'valorant' AND pgp.profile_visible = TRUE
            ))
              AND (pgs.provider_player_id IS DISTINCT FROM ra.puuid OR (
                (pgs.last_act_id IS NULL OR pgs.synced_at IS NULL OR pgs.synced_at < now() - INTERVAL '1 hour')
                AND (pgs.sync_requested_at IS NULL OR pgs.sync_requested_at < now() - INTERVAL '1 hour')))
              AND (pgs.updated_at IS NULL OR pgs.updated_at < now() - INTERVAL '10 minutes')
            ORDER BY COALESCE(pgs.updated_at, '1970-01-01'::timestamptz), ra.user_id
            LIMIT @batchSize
            """,
            new { batchSize = BatchSize })).ToList();

        if (stale.Count == 0) return;

        logger.LogInformation("[ValorantStatsSync] Syncing {Count} user(s)", stale.Count);

        foreach (var userId in stale)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await syncRequests.RequestAsync(userId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[ValorantStatsSync] Failed for user {UserId}", userId);
            }
        }
    }
}
