using System.Data;
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

    // ── PROJ-078: Org bracket defaults ────────────────────────────────────

    /// <summary>
    /// Returns the org's default bracket max teams, or 32 (platform default)
    /// when orgId is null (solo organizer without an organization).
    /// 0 means unlimited.
    /// </summary>
    public async Task<int> GetOrgBracketMaxAsync(
        Guid? orgId,
        IDbConnection? existingConn = null,
        IDbTransaction? existingTx = null,
        CancellationToken ct = default)
    {
        if (!orgId.HasValue)
            return 32;

        var ownConn = existingConn is null;
        var conn = existingConn ?? db.CreateConnection();
        try
        {
            return await conn.ExecuteScalarAsync<int>(
                "SELECT COALESCE(default_bracket_max_teams, 32) FROM organizations WHERE id = @orgId",
                new { orgId = orgId.Value },
                existingTx);
        }
        finally
        {
            if (ownConn) conn.Dispose();
        }
    }

    // ── BE-1: ReserveNew ────────────────────────────────────────────────────

    /// <summary>
    /// Reserves a new license for a tournament immediately after creation.
    /// Checks against the org's default_bracket_max_teams (PROJ-078) instead
    /// of a hardcoded 32-team cap. Solo organizers without an org default to 32.
    /// The monthly quota trigger has been removed (PROJ-077); quota is now enforced at publish time.
    /// </summary>
    public async Task<LicenseResult<EventLicense>> ReserveNewAsync(
        Guid tournamentId,
        Guid userId,
        Guid? organizationId,
        int maxTeams,
        IDbConnection? existingConn = null,
        IDbTransaction? existingTx = null,
        CancellationToken ct = default)
    {
        var orgMax = await GetOrgBracketMaxAsync(organizationId, existingConn, existingTx, ct);
        var isUnlimited = orgMax == 0;
        if (!isUnlimited && (maxTeams == 0 || maxTeams > orgMax))
            return LicenseResult<EventLicense>.Err("bracket_upgrade_required");

        var tier = maxTeams == 0 ? "custom" : ResolveBracketTier(maxTeams);

        var ownConn = existingConn is null;
        var conn = existingConn ?? db.CreateConnection();
        try
        {
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
                new { tournamentId, userId, organizationId, tier, maxTeams },
                existingTx);

            return LicenseResult<EventLicense>.Ok(license);
        }
        finally
        {
            if (ownConn) conn.Dispose();
        }
    }

    // ── PROJ-077: Publish-time quota check ─────────────────────────────────

    /// <summary>
    /// Checks whether the organizer may publish another free-quota event this month.
    /// Counts published (non-draft, non-cancelled) free-quota events in the current
    /// calendar month and compares against the org's limit (default 2).
    /// </summary>
    public async Task<PublishQuotaResult> CheckPublishQuotaAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var usedThisMonth = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)::int FROM event_licenses el
            JOIN tournaments t ON t.id = el.tournament_id
            WHERE el.user_id = @userId
              AND el.is_free_quota = TRUE
              AND t.status NOT IN ('draft', 'cancelled')
              AND date_trunc('month', el.created_at) = date_trunc('month', NOW())
            """,
            new { userId });

        var monthlyLimit = await GetOrganizationQuotaLimitAsync(conn, organizationId);

        var now = DateTime.UtcNow;
        var resetsAt = new DateTime(
            now.Month == 12 ? now.Year + 1 : now.Year,
            now.Month == 12 ? 1 : now.Month + 1,
            1, 0, 0, 0, DateTimeKind.Utc)
            .ToString("yyyy-MM-ddTHH:mm:ssZ");

        return new PublishQuotaResult(
            IsAllowed: usedThisMonth < monthlyLimit,
            UsedThisMonth: usedThisMonth,
            MonthlyLimit: monthlyLimit,
            ResetsAt: resetsAt);
    }

    /// <summary>
    /// Returns the monthly event quota limit for an organization.
    /// NULL in the database means the default of 2.
    /// </summary>
    public async Task<int> GetOrganizationQuotaLimitAsync(
        IDbConnection conn,
        Guid organizationId) =>
        await conn.ExecuteScalarAsync<int>(
            "SELECT COALESCE(monthly_event_quota, 2) FROM organizations WHERE id = @orgId",
            new { orgId = organizationId });

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

    // ── BE-10+: UpsertLicenseAsync ──────────────────────────────────────────

    /// <summary>
    /// Admin operation: set the bracket tier and Organizer Pro flag together.
    /// UPSERTs the license row. Invalidates cache after write.
    /// </summary>
    public async Task UpsertLicenseAsync(
        Guid tournamentId,
        string bracketTier,
        bool hasOrganizerPro,
        CancellationToken ct = default)
    {
        var maxTeams = ResolveBracketMaxTeams(bracketTier);
        using var conn = db.CreateConnection();

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
                (@tournamentId, @userId, @orgId, @tier, @maxTeams,
                 @hasOrganizerPro, false, 'active')
            ON CONFLICT (tournament_id)
            DO UPDATE SET
                bracket_tier      = @tier,
                bracket_max_teams = @maxTeams,
                has_organizer_pro = @hasOrganizerPro
            """,
            new
            {
                tournamentId,
                userId = tournamentMeta.Value.UserId,
                orgId = tournamentMeta.Value.OrgId,
                tier = bracketTier,
                maxTeams,
                hasOrganizerPro,
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

    internal static int ResolveBracketMaxTeams(string tier) => tier switch
    {
        "<=64" => 64,
        "<=128" => 128,
        "<=256" => 256,
        "<=512" => 512,
        "custom" => 0,
        _ => 32,
    };
}
