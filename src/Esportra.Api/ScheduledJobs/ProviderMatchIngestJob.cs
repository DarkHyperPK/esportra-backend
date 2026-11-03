using Dapper;
using Esportra.Api.Services;
using Esportra.Api.Services.ProviderMatches;
using Esportra.Contracts.Database;
using Esportra.Infrastructure.Integrations;
using Hangfire;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.ScheduledJobs;

public interface IProviderMatchIngestTrigger
{
    void Queue(Guid associationId);
}

public sealed class ProviderMatchIngestTrigger(IBackgroundJobClient jobs) : IProviderMatchIngestTrigger
{
    public void Queue(Guid associationId) =>
        jobs.Enqueue<ProviderMatchIngestJob>(j => j.RunAsync(associationId, 0, CancellationToken.None));
}

/// <summary>
/// Fetches a reported Riot match on the server (the client's copy is never trusted), stores it once, normalizes it and
/// verifies it against the rosters frozen at report time. Riot can list a custom game minutes late, so a missing match
/// is looked for again after a delay; a stored payload is re-derived without calling Riot.
/// </summary>
public sealed class ProviderMatchIngestJob(
    IDbConnectionFactory db, RiotApiClient riot, HybridCache cache, IBackgroundJobClient jobs,
    Microsoft.Extensions.Options.IOptions<RiotMatchOptions> matchOptions, ILogger<ProviderMatchIngestJob> logger)
{
    public static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)];

    private sealed record Row(Guid MatchId, string ProviderMatchId, string VerificationStatus, string? Shard);

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid associationId, int attempt, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            """
            SELECT a.match_id AS MatchId, m.provider_match_id AS ProviderMatchId,
                   a.verification_status AS VerificationStatus, m.shard AS Shard
            FROM public.provider_match_associations a
            JOIN public.provider_matches m ON m.id = a.match_id
            WHERE a.id = @associationId
            """, new { associationId });
        if (row is null || row.VerificationStatus == "duplicate") return;

        var stored = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT payload::text FROM public.provider_match_payloads WHERE match_id = @MatchId", new { row.MatchId });
        if (stored is not null)
        {
            await ProviderMatchIngest.ApplyAsync(conn, associationId, stored, row.Shard ?? "eu", storePayload: false, matchOptions.Value.AllowNonCustomMatches);
            return;
        }

        var puuid = await conn.QueryFirstOrDefaultAsync<string>(
            """
            SELECT puuid FROM public.provider_match_roster_snapshots
            WHERE association_id = @associationId AND puuid IS NOT NULL ORDER BY team_slot, snapshot_at, id LIMIT 1
            """, new { associationId });
        if (puuid is null)
        {
            await ProviderMatchIngest.MarkNoLinkedAccountsAsync(conn, associationId, row.MatchId);
            return;
        }

        var shard = await RiotShards.ResolveAsync(riot, cache, puuid, null, ct);
        var (status, body) = await riot.GetMatchAsync(row.ProviderMatchId, shard, ct);
        if (status == 200)
        {
            var outcome = await ProviderMatchIngest.ApplyAsync(conn, associationId, body, shard, storePayload: true, matchOptions.Value.AllowNonCustomMatches);
            if (outcome is { Failure: null, MatchCompleted: false }) RetryOrGiveUp(associationId, attempt);
            logger.LogInformation("Provider match {MatchId} ingested: {Status}", row.ProviderMatchId, outcome.VerificationStatus);
            return;
        }

        var retryable = status is 404 or 429 or >= 500;
        if (retryable && attempt < RetryDelays.Length)
        {
            await ProviderMatchStore.SetStatusAsync(conn, null, row.MatchId, "pending", $"riot_{status}", countAttempt: true);
            RetryOrGiveUp(associationId, attempt);
            return;
        }
        var final = status == 404 ? "unavailable" : "failed";
        await ProviderMatchStore.SetStatusAsync(conn, null, row.MatchId, final, $"riot_{status}", countAttempt: true);
        logger.LogWarning("Provider match {MatchId} not retrieved (Riot {Status})", row.ProviderMatchId, status);
    }

    private void RetryOrGiveUp(Guid associationId, int attempt)
    {
        if (attempt < RetryDelays.Length)
            jobs.Schedule<ProviderMatchIngestJob>(j => j.RunAsync(associationId, attempt + 1, CancellationToken.None), RetryDelays[attempt]);
    }
}
