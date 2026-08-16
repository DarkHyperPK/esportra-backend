using System.Net.Http.Headers;
using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Api.Services;

public sealed class SponsorAssetCleanupService(
    IDbConnectionFactory connectionFactory,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<SponsorAssetCleanupService> logger)
{
    public async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var connection = connectionFactory.CreateConnection();
        await PurgeExpiredPlacementsAsync(connection, ct);
        await EnqueueExpiredAssetsAsync(connection, ct);
        using var transaction = connection.BeginTransaction();
        var leaseOwner = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        var jobs = (await connection.QueryAsync<CleanupJob>(new CommandDefinition(
            """
            WITH candidates AS (
                SELECT id FROM sponsor_asset_cleanup_jobs
                WHERE completed_at IS NULL AND failed_at IS NULL AND next_attempt_at <= NOW()
                  AND (lease_expires_at IS NULL OR lease_expires_at < NOW())
                ORDER BY created_at
                LIMIT 20
                FOR UPDATE SKIP LOCKED
            )
            UPDATE sponsor_asset_cleanup_jobs job
            SET lease_owner = @leaseOwner, lease_expires_at = NOW() + INTERVAL '5 minutes'
            FROM candidates
            WHERE job.id = candidates.id
            RETURNING job.id, job.asset_id AS AssetId, job.bucket, job.object_path AS ObjectPath, job.attempts
            """, new { leaseOwner }, transaction, cancellationToken: ct))).AsList();
        transaction.Commit();

        foreach (var job in jobs) await ProcessJobAsync(connection, job, leaseOwner, ct);
    }

    private async Task PurgeExpiredPlacementsAsync(System.Data.IDbConnection connection, CancellationToken ct)
    {
        var expired = (await connection.QueryAsync<ExpiredPlacement>(new CommandDefinition(
            """
            DELETE FROM public.sponsor_placements
            WHERE ends_at IS NOT NULL AND ends_at <= NOW()
            RETURNING id, sponsor_id, tournament_id, placement_zone, slot_number, banner_asset_id, logo_asset_id
            """, cancellationToken: ct))).AsList();

        if (expired.Count == 0) return;
        logger.LogInformation("Purged {Count} expired placement(s)", expired.Count);

        var supabaseUrl = configuration["Supabase:Url"]?.TrimEnd('/');
        var serviceKey = configuration["Supabase:ServiceKey"];

        foreach (var p in expired)
        {
            var assetIds = new[] { p.BannerAssetId, p.LogoAssetId }
                .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();

            if (assetIds.Length > 0 && supabaseUrl is not null && serviceKey is not null)
            {
                var assets = (await connection.QueryAsync<AssetPath>(new CommandDefinition(
                    "SELECT bucket, object_path FROM sponsor_placement_assets WHERE id = ANY(@assetIds)",
                    new { assetIds }, cancellationToken: ct))).AsList();

                var client = httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
                client.DefaultRequestHeaders.Add("apikey", serviceKey);

                foreach (var asset in assets)
                {
                    var escapedPath = string.Join('/', asset.ObjectPath.Split('/').Select(Uri.EscapeDataString));
                    using var response = await client.DeleteAsync(
                        $"{supabaseUrl}/storage/v1/object/{Uri.EscapeDataString(asset.Bucket)}/{escapedPath}", ct);
                }

                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM sponsor_placement_assets WHERE id = ANY(@assetIds)",
                    new { assetIds }, cancellationToken: ct));
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO public.sponsor_placement_audit_log
                    (placement_id, sponsor_id, tournament_id, placement_zone, slot_number, action, details)
                VALUES (@Id, @SponsorId, @TournamentId, @PlacementZone, @SlotNumber, 'expired', '{}')
                """, p, cancellationToken: ct));
        }
    }

    private sealed record AssetPath
    {
        public string Bucket { get; init; } = "";
        public string ObjectPath { get; init; } = "";
    }

    private sealed record ExpiredPlacement
    {
        public Guid Id { get; init; }
        public Guid SponsorId { get; init; }
        public Guid? TournamentId { get; init; }
        public string PlacementZone { get; init; } = "";
        public int? SlotNumber { get; init; }
        public Guid? BannerAssetId { get; init; }
        public Guid? LogoAssetId { get; init; }
    }

    internal static Task EnqueueExpiredAssetsAsync(System.Data.IDbConnection connection, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            WITH expired AS (
                SELECT id, bucket, object_path FROM sponsor_placement_assets
                WHERE claimed_at IS NULL AND deleting_at IS NULL AND expires_at <= NOW()
                ORDER BY expires_at LIMIT 100 FOR UPDATE SKIP LOCKED
            )
            INSERT INTO sponsor_asset_cleanup_jobs (asset_id, bucket, object_path)
            SELECT id, bucket, object_path FROM expired
            ON CONFLICT (bucket, object_path) WHERE completed_at IS NULL AND failed_at IS NULL DO NOTHING
            """, cancellationToken: ct));

    private async Task ProcessJobAsync(System.Data.IDbConnection connection, CleanupJob job, string leaseOwner, CancellationToken ct)
    {
        try
        {
            using var transaction = connection.BeginTransaction();
            var isReferenced = job.AssetId.HasValue && await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                """
                SELECT EXISTS(SELECT 1 FROM sponsor_placements WHERE banner_asset_id = @AssetId OR logo_asset_id = @AssetId)
                FROM sponsor_placement_assets WHERE id = @AssetId FOR UPDATE
                """,
                new { job.AssetId }, transaction, cancellationToken: ct));
            if (isReferenced)
            {
                transaction.Commit();
                await CompleteAsync(connection, job.Id, null, leaseOwner, ct);
                return;
            }
            if (job.AssetId.HasValue)
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE sponsor_placement_assets SET deleting_at = NOW() WHERE id = @AssetId",
                    new { job.AssetId }, transaction, cancellationToken: ct));
            transaction.Commit();
            var supabaseUrl = configuration["Supabase:Url"]?.TrimEnd('/')
                ?? throw new InvalidOperationException("Supabase:Url not configured");
            var serviceKey = configuration["Supabase:ServiceKey"]
                ?? throw new InvalidOperationException("Supabase:ServiceKey not configured");
            var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
            client.DefaultRequestHeaders.Add("apikey", serviceKey);
            var escapedPath = string.Join('/', job.ObjectPath.Split('/').Select(Uri.EscapeDataString));
            using var response = await client.DeleteAsync($"{supabaseUrl}/storage/v1/object/{Uri.EscapeDataString(job.Bucket)}/{escapedPath}", ct);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                throw new HttpRequestException($"Storage returned {(int)response.StatusCode}");

            await CompleteAsync(connection, job.Id, job.AssetId, leaseOwner, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var delayMinutes = Math.Min(60, 1 << Math.Min(job.Attempts, 5));
            var isTerminal = job.Attempts + 1 >= 10;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE sponsor_asset_cleanup_jobs
                SET attempts = attempts + 1, next_attempt_at = NOW() + make_interval(mins => @delayMinutes),
                    last_error = @error, failed_at = CASE WHEN @isTerminal THEN NOW() ELSE NULL END,
                    lease_owner = NULL, lease_expires_at = NULL
                WHERE id = @id AND lease_owner = @leaseOwner
                """,
                new { job.Id, leaseOwner, delayMinutes, isTerminal, error = exception.Message[..Math.Min(exception.Message.Length, 500)] },
                cancellationToken: ct));
            if (isTerminal) logger.LogError(exception, "Sponsor asset cleanup job {JobId} reached terminal failure", job.Id);
        }
    }

    private static async Task CompleteAsync(System.Data.IDbConnection connection, Guid id, Guid? assetId, string leaseOwner, CancellationToken ct)
    {
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE sponsor_asset_cleanup_jobs SET completed_at = NOW(), attempts = attempts + 1, last_error = NULL, lease_owner = NULL, lease_expires_at = NULL WHERE id = @id AND lease_owner = @leaseOwner",
            new { id, leaseOwner }, transaction, cancellationToken: ct));
        if (assetId.HasValue)
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM sponsor_placement_assets asset WHERE asset.id = @assetId AND NOT EXISTS (SELECT 1 FROM sponsor_placements placement WHERE placement.banner_asset_id = asset.id OR placement.logo_asset_id = asset.id)",
                new { assetId }, transaction, cancellationToken: ct));
        transaction.Commit();
    }

    internal sealed record CleanupJob(Guid Id, Guid? AssetId, string Bucket, string ObjectPath, int Attempts);
}
