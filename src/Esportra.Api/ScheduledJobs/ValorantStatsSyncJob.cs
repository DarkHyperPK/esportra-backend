using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Hangfire;

namespace Esportra.Api.ScheduledJobs;

[Queue("default")]
public sealed class ValorantStatsSyncJob(
    IDbConnectionFactory db,
    ValorantStatsSyncService syncService,
    ILogger<ValorantStatsSyncJob> logger)
{
    private const int BatchSize = 20;

    public async Task ExecuteAsync(CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        var userIds = (await conn.QueryAsync<Guid>(
            """
            SELECT DISTINCT ra.user_id
            FROM public.riot_accounts ra
            WHERE EXISTS (
                SELECT 1 FROM public.lfg_listings l
                WHERE l.user_id = ra.user_id AND l.status = 'published'
            ) OR EXISTS (
                SELECT 1 FROM public.player_game_profiles pgp
                WHERE pgp.user_id = ra.user_id AND pgp.profile_visible = TRUE
            )
            ORDER BY ra.user_id
            LIMIT @batchSize
            """,
            new { batchSize = BatchSize * 3 })).ToList();

        if (userIds.Count == 0) return;

        var stale = (await conn.QueryAsync<Guid>(
            """
            SELECT u.user_id
            FROM unnest(@userIds) AS u(user_id)
            LEFT JOIN public.player_game_stats pgs
                ON pgs.user_id = u.user_id AND pgs.game_slug = 'valorant'
            WHERE pgs.synced_at IS NULL
               OR pgs.synced_at < now() - INTERVAL '1 hour'
            ORDER BY COALESCE(pgs.synced_at, '1970-01-01'::timestamptz) ASC
            LIMIT @batchSize
            """,
            new { userIds = userIds.ToArray(), batchSize = BatchSize })).ToList();

        if (stale.Count == 0) return;

        logger.LogInformation("[ValorantStatsSync] Syncing {Count} user(s)", stale.Count);

        foreach (var userId in stale)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await syncService.SyncForUserAsync(userId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[ValorantStatsSync] Failed for user {UserId}", userId);
            }
        }
    }
}
