using Dapper;
using Esportra.Contracts.Database;
using Esportra.Core.Licensing;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Services;

/// <summary>
/// Manages event licenses (PROJ-058). Scoped per request.
///
/// Legacy rule: a null license row means the tournament was created before the
/// licensing system and is grandfathered — IsOrganizerPro returns true for it.
/// </summary>
public class EventLicenseService(
    IDbConnectionFactory db,
    HybridCache cache,
    IConfiguration config)
{
    private const string CacheKeyPrefix = "event-lic:";
    private static readonly HybridCacheEntryOptions CacheOpts =
        new() { Expiration = TimeSpan.FromSeconds(60) };

    public string ContactUsUrl =>
        config["Licensing:ContactUsUrl"] ?? "https://esportra.com/contact";

    // ── BE-1: ReserveNew ────────────────────────────────────────────────────

    /// <summary>
    /// Reserves a new license for a tournament immediately after creation.
    /// Returns bracket_upgrade_required when maxTeams > 32 (no INSERT).
    /// Returns quota_exhausted when the trigger fires (INSERT attempted, DB rejects).
    /// </summary>
    public async Task<LicenseResult<EventLicense>> ReserveNewAsync(
        Guid tournamentId,
        Guid userId,
        Guid organizationId,
        int maxTeams,
        CancellationToken ct = default)
    {
        if (maxTeams > 32)
            return LicenseResult<EventLicense>.Err("bracket_upgrade_required");

        var tier = ResolveBracketTier(maxTeams);

        try
        {
            using var conn = db.CreateConnection();
            var license = await conn.QuerySingleAsync<EventLicense>(
                """
                INSERT INTO event_licenses
                    (tournament_id, user_id, organization_id, bracket_tier, bracket_max_teams,
                     has_organizer_pro, is_free_quota, status)
                VALUES
                    (@tournamentId, @userId, @organizationId, @tier, @maxTeams,
                     false, true, 'active')
                RETURNING
                    id              AS Id,
                    tournament_id   AS TournamentId,
                    user_id         AS UserId,
                    organization_id AS OrganizationId,
                    bracket_tier    AS BracketTier,
                    bracket_max_teams AS BracketMaxTeams,
                    has_organizer_pro AS HasOrganizerPro,
                    is_free_quota   AS IsFreeQuota,
                    status          AS Status,
                    created_at      AS CreatedAt
                """,
                new { tournamentId, userId, organizationId, tier, maxTeams });

            return LicenseResult<EventLicense>.Ok(license);
        }
        catch (Npgsql.PostgresException ex) when (ex.Message.Contains("quota_exhausted"))
        {
            return LicenseResult<EventLicense>.Err("quota_exhausted");
        }
    }

    // ── BE-1: GetForTournament ──────────────────────────────────────────────

    /// <summary>
    /// Returns the active license for a tournament, or null for legacy tournaments.
    /// Cached 60 seconds per tournament ID. Virtual for unit test overriding.
    /// </summary>
    public virtual async Task<EventLicense?> GetForTournamentAsync(
        Guid tournamentId,
        CancellationToken ct = default) =>
        await cache.GetOrCreateAsync<EventLicense?>(
            $"{CacheKeyPrefix}{tournamentId}",
            async innerCt =>
            {
                using var conn = db.CreateConnection();
                return await conn.QuerySingleOrDefaultAsync<EventLicense>(
                    """
                    SELECT
                        id              AS Id,
                        tournament_id   AS TournamentId,
                        user_id         AS UserId,
                        organization_id AS OrganizationId,
                        bracket_tier    AS BracketTier,
                        bracket_max_teams AS BracketMaxTeams,
                        has_organizer_pro AS HasOrganizerPro,
                        is_free_quota   AS IsFreeQuota,
                        status          AS Status,
                        created_at      AS CreatedAt
                    FROM event_licenses
                    WHERE tournament_id = @tournamentId
                    """,
                    new { tournamentId });
            },
            CacheOpts,
            cancellationToken: ct);

    // ── BE-1: IsOrganizerPro ────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the tournament has Organizer Pro enabled, or when the
    /// tournament has no license row (legacy/grandfathered).
    /// </summary>
    public async Task<bool> IsOrganizerProAsync(
        Guid tournamentId,
        CancellationToken ct = default)
    {
        var license = await GetForTournamentAsync(tournamentId, ct);
        return license is null || license.HasOrganizerPro;
    }

    // ── BE-10: UpsertOrganizerPro ───────────────────────────────────────────

    /// <summary>
    /// Admin operation: enable or disable Organizer Pro for a tournament.
    /// UPSERTs the license row (inserts sensible defaults when no row exists).
    /// Invalidates the cache after the write.
    /// </summary>
    public async Task UpsertOrganizerProAsync(
        Guid tournamentId,
        bool enabled,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        // We need the user_id and org_id for the INSERT path; fall back gracefully.
        var tournamentMeta = await conn.QuerySingleOrDefaultAsync<(Guid OrgId, Guid UserId)?>(
            """
            SELECT organization_id AS OrgId, organizer_id AS UserId
            FROM tournaments
            WHERE id = @tournamentId
            """,
            new { tournamentId });

        if (tournamentMeta is null)
            throw new InvalidOperationException($"Tournament {tournamentId} not found.");

        await conn.ExecuteAsync(
            """
            INSERT INTO event_licenses
                (tournament_id, user_id, organization_id, bracket_tier, bracket_max_teams,
                 has_organizer_pro, is_free_quota, status)
            VALUES
                (@tournamentId, @userId, @orgId, 'free', 32,
                 @enabled, false, 'active')
            ON CONFLICT (tournament_id)
            DO UPDATE SET has_organizer_pro = @enabled
            """,
            new
            {
                tournamentId,
                userId = tournamentMeta.Value.UserId,
                orgId = tournamentMeta.Value.OrgId,
                enabled,
            });

        await cache.RemoveAsync($"{CacheKeyPrefix}{tournamentId}", ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    internal static string ResolveBracketTier(int maxTeams) => maxTeams switch
    {
        <= 32 => "free",
        <= 64 => "<=64",
        <= 128 => "<=128",
        <= 256 => "<=256",
        <= 512 => "<=512",
        _ => "custom",
    };
}
