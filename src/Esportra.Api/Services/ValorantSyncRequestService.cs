using System.Data;
using Dapper;
using Esportra.Contracts.Database;
using Hangfire;

namespace Esportra.Api.Services;

/// <summary>One durable request per linked player, shared across all listing viewers.</summary>
public sealed class ValorantSyncRequestService(IDbConnectionFactory db, IBackgroundJobClient jobs,
    ILogger<ValorantSyncRequestService> logger)
{
    public sealed record Result(string? Status, int RetryAfterSeconds, DateTime? SyncedAt = null);
    private sealed record State(string? Status, DateTime? UpdatedAt, DateTime? RequestedAt, bool SameAccount, DateTime? SyncedAt);
    private sealed record Claim(Result Response, DateTime? RequestedAt = null);

    public async Task<Result> GetStateAsync(Guid userId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var state = await conn.QuerySingleOrDefaultAsync<State>(new CommandDefinition("""
            SELECT p.sync_status AS status, p.updated_at AS updatedat, p.sync_requested_at AS requestedat,
                true AS sameaccount, CASE WHEN p.last_act_id IS NOT NULL THEN p.synced_at END AS syncedat
            FROM public.riot_accounts ra LEFT JOIN public.player_game_stats p
                ON p.user_id = ra.user_id AND p.game_slug = 'valorant' AND p.provider_player_id = ra.puuid
            WHERE ra.user_id = @userId
            """, new { userId }, cancellationToken: ct));
        return state is null ? new("unlinked", 0) : new(
            ValorantStatsFreshness.Remaining(state.SyncedAt) > 0 ? "cached" : state.Status,
            ValorantStatsFreshness.Remaining(state.SyncedAt, state.RequestedAt), state.SyncedAt);
    }

    public async Task<Result> RequestAsync(Guid userId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var claim = await TryClaimAsync(conn, userId, ct);
        if (claim.RequestedAt is null)
        {
            if (claim.Response.Status == "cached") await ValorantHeadshotRecovery.RepairSavedAsync(conn, userId, ct);
            return claim.Response;
        }
        try
        {
            jobs.Enqueue<ValorantStatsSyncService>(service => service.SyncForUserAsync(userId, true, CancellationToken.None));
            return claim.Response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not enqueue requested Valorant stats sync");
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE public.player_game_stats SET sync_status = 'error', sync_requested_at = NULL, updated_at = now()
                WHERE user_id = @userId AND game_slug = 'valorant' AND sync_status = 'pending'
                    AND sync_requested_at = @requestedAt
                """, new { userId, requestedAt = claim.RequestedAt }, cancellationToken: CancellationToken.None));
            return new("error", 0);
        }
    }

    private async Task<Claim> TryClaimAsync(IDbConnection conn, Guid userId, CancellationToken ct)
    {
        using var tx = conn.BeginTransaction();
        var locked = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT pg_try_advisory_xact_lock(hashtextextended(@key, 0))",
            new { key = $"valorant-stats:{userId}" }, tx, cancellationToken: ct));
        if (!locked)
        {
            var current = await GetStateAsync(userId, ct);
            return new(current.Status == "cached" ? current : current with { Status = "syncing", RetryAfterSeconds = 15 });
        }

        var puuid = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT puuid FROM public.riot_accounts WHERE user_id = @userId", new { userId }, tx, cancellationToken: ct));
        if (string.IsNullOrWhiteSpace(puuid)) return new(new("unlinked", 0));
        var state = await ReadStateAsync(conn, tx, userId, puuid, ct);
        var now = DateTime.UtcNow;
        if (state?.SameAccount == true && ValorantStatsFreshness.Remaining(state.SyncedAt) > 0)
            return new(new("cached", ValorantStatsFreshness.Remaining(state.SyncedAt, state.RequestedAt), state.SyncedAt));
        if (state is { SameAccount: true, Status: "pending" or "syncing", UpdatedAt: not null, RequestedAt: not null } &&
            now - state.UpdatedAt.Value < TimeSpan.FromMinutes(10)) return new(new(state.Status, 15, state.SyncedAt));
        if (state?.SameAccount == true && ValorantStatsFreshness.Remaining(state.RequestedAt) > 0)
            return new(new("cooldown", ValorantStatsFreshness.Remaining(state.RequestedAt), state.SyncedAt));

        var requestedAt = await conn.QuerySingleAsync<DateTime>(new CommandDefinition("""
            INSERT INTO public.player_game_stats (user_id, game_slug, provider_player_id, sync_status, sync_requested_at, synced_at)
            VALUES (@userId, 'valorant', @puuid, 'pending', now(), NULL)
            ON CONFLICT (user_id, game_slug) DO UPDATE SET sync_status = 'pending', sync_error = NULL,
                sync_requested_at = now(), updated_at = now()
            RETURNING sync_requested_at
            """, new { userId, puuid }, tx, cancellationToken: ct));
        // Commit before dispatch: a worker must be able to acquire the player's sync lock.
        tx.Commit();
        return new(new("queued", 3600, state?.SameAccount == true ? state.SyncedAt : null), requestedAt);
    }

    private static Task<State?> ReadStateAsync(IDbConnection conn, IDbTransaction tx, Guid userId, string puuid, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<State>(new CommandDefinition("""
            SELECT sync_status AS status, updated_at AS updatedat, sync_requested_at AS requestedat,
                   provider_player_id = @puuid AS sameaccount,
                   CASE WHEN last_act_id IS NOT NULL THEN synced_at END AS syncedat
            FROM public.player_game_stats WHERE user_id = @userId AND game_slug = 'valorant'
            """, new { userId, puuid }, tx, cancellationToken: ct));
}
