namespace Esportra.Core.Seasons;

using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;

/// <summary>
/// Service for managing season-tournament mappings with role-based organization.
/// </summary>
public class SeasonTournamentService
{
    private readonly IDbConnectionFactory db;
    private readonly AuditService auditService;

    public SeasonTournamentService(IDbConnectionFactory db, AuditService auditService)
    {
        this.db = db;
        this.auditService = auditService;
    }

    /// <summary>
    /// Adds a tournament to a season with a specific role.
    /// </summary>
    public async Task<Guid> AddTournamentAsync(
        Guid seasonId,
        Guid tournamentId,
        string role,
        string? region,
        string? displayName,
        int sortOrder,
        UserContext actor,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        // Validate tournament exists
        var tournamentExists = await conn.ExecuteScalarAsync<bool>(
            "SELECT 1 FROM tournaments WHERE id = @id", new { id = tournamentId }, cancellationToken: ct);

        if (!tournamentExists)
            throw new InvalidOperationException($"Tournament {tournamentId} not found");

        // Validate season exists
        var seasonExists = await conn.ExecuteScalarAsync<bool>(
            "SELECT 1 FROM seasons WHERE id = @id", new { id = seasonId }, cancellationToken: ct);

        if (!seasonExists)
            throw new InvalidOperationException($"Season {seasonId} not found");

        // Validate role constraints (only one grand_final per season)
        if (role == "grand_final")
        {
            var existingGrandFinal = await conn.ExecuteScalarAsync<bool>(
                "SELECT 1 FROM season_tournaments WHERE season_id = @seasonId AND role = 'grand_final'",
                new { seasonId }, cancellationToken: ct);

            if (existingGrandFinal)
                throw new InvalidOperationException("Season already has a grand final");
        }

        var seasonTournamentId = Guid.NewGuid();

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO season_tournaments (
                id, season_id, tournament_id, role, region, display_name, sort_order, status, created_at, updated_at
            )
            VALUES (@id, @seasonId, @tournamentId, @role, @region, @displayName, @sortOrder, 'draft', NOW(), NOW())
            """, new
            {
                id = seasonTournamentId,
                seasonId,
                tournamentId,
                role,
                region,
                displayName,
                sortOrder,
            }, cancellationToken: ct));

        await auditService.LogAsync(
            seasonId,
            actor.UserId,
            "tournament_created",
            "season_tournament",
            seasonTournamentId,
            before: null,
            after: JsonSerializer.Serialize(new { tournamentId, role, region, displayName }),
            reason: $"Tournament {tournamentId} added to season with role {role}",
            ipAddress: actor.IpAddress,
            userAgent: actor.UserAgent,
            ct);

        return seasonTournamentId;
    }

    /// <summary>
    /// Updates a season-tournament mapping.
    /// </summary>
    public async Task UpdateTournamentAsync(
        Guid seasonTournamentId,
        string? role,
        string? region,
        string? displayName,
        int? sortOrder,
        UserContext actor,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var current = await conn.QuerySingleOrDefaultAsync<dynamic>(@"
            SELECT st.*, s.id as season_id FROM season_tournaments st
            JOIN seasons s ON s.id = st.season_id
            WHERE st.id = @id", new { id = seasonTournamentId }, cancellationToken: ct);

        if (current == null)
            throw new InvalidOperationException($"Season tournament {seasonTournamentId} not found");

        // Validate role constraints if changing to grand_final
        if (role == "grand_final" && current.role != "grand_final")
        {
            var existingGrandFinal = await conn.ExecuteScalarAsync<bool>(
                "SELECT 1 FROM season_tournaments WHERE season_id = @seasonId AND role = 'grand_final' AND id != @id",
                new { seasonId = current.season_id, id = seasonTournamentId }, cancellationToken: ct);

            if (existingGrandFinal)
                throw new InvalidOperationException("Season already has a grand final");
        }

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE season_tournaments
            SET role = COALESCE(@role, role),
                region = @region,
                display_name = @displayName,
                sort_order = COALESCE(@sortOrder, sort_order),
                updated_at = NOW()
            WHERE id = @id
            """, new
            {
                id = seasonTournamentId,
                role,
                region,
                displayName,
                sortOrder,
            }, cancellationToken: ct));

        await auditService.LogAsync(
            current.season_id,
            actor.UserId,
            "tournament_updated",
            "season_tournament",
            seasonTournamentId,
            before: JsonSerializer.Serialize(current),
            after: JsonSerializer.Serialize(new { role, region, displayName, sortOrder }),
            reason: "Season tournament configuration updated",
            ipAddress: actor.IpAddress,
            userAgent = actor.UserAgent,
            ct);
    }

    /// <summary>
    /// Removes a tournament from a season.
    /// </summary>
    public async Task RemoveTournamentAsync(Guid seasonTournamentId, UserContext actor, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var current = await conn.QuerySingleOrDefaultAsync<dynamic>(@"
            SELECT st.*, s.id as season_id FROM season_tournaments st
            JOIN seasons s ON s.id = st.season_id
            WHERE st.id = @id", new { id = seasonTournamentId }, cancellationToken: ct);

        if (current == null)
            throw new InvalidOperationException($"Season tournament {seasonTournamentId} not found");

        // Check if tournament has started (safe to remove only if draft/scheduled)
        var tournamentStatus = await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM tournaments WHERE id = @tournamentId",
            new { tournamentId = current.tournament_id }, cancellationToken: ct);

        if (tournamentStatus != null && tournamentStatus != "draft")
            throw new InvalidOperationException($"Cannot remove tournament with status {tournamentStatus}");

        await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM season_tournaments WHERE id = @id
            """, new { id = seasonTournamentId }, cancellationToken: ct));

        await auditService.LogAsync(
            current.season_id,
            actor.UserId,
            "stage_deleted",
            "season_tournament",
            seasonTournamentId,
            before: JsonSerializer.Serialize(current),
            after: null,
            reason: "Tournament removed from season",
            ipAddress: actor.IpAddress,
            userAgent = actor.UserAgent,
            ct);
    }

    /// <summary>
    /// Reorders tournaments within a season.
    /// </summary>
    public async Task ReorderAsync(
        Guid seasonId,
        Dictionary<Guid, int> sortOrderUpdates,
        UserContext actor,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        foreach (var (seasonTournamentId, newSortOrder) in sortOrderUpdates)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE season_tournaments
                SET sort_order = @sortOrder, updated_at = NOW()
                WHERE id = @id AND season_id = @seasonId
                """, new
                {
                    id = seasonTournamentId,
                    sortOrder = newSortOrder,
                    seasonId,
                }, cancellationToken: ct);
        }

        await auditService.LogAsync(
            seasonId,
            actor.UserId,
            "tournament_updated",
            "season",
            seasonId,
            before: null,
            after: JsonSerializer.Serialize(new { reorder_updates = sortOrderUpdates }),
            reason: "Tournaments reordered within season",
            ipAddress: actor.IpAddress,
            userAgent = actor.UserAgent,
            ct);
    }

    /// <summary>
    /// Applies bulk configuration to multiple tournaments in a season.
    /// </summary>
    public async Task BulkConfigAsync(
        Guid seasonId,
        List<Guid> seasonTournamentIds,
        Dictionary<string, object> configUpdates,
        UserContext actor,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        foreach (var seasonTournamentId in seasonTournamentIds)
        {
            var setClauses = new List<string>();
            var parameters = new Dictionary<string, object> { { "id", seasonTournamentId } };

            if (configUpdates.ContainsKey("region"))
            {
                setClauses.Add("region = @region");
                parameters["region"] = configUpdates["region"];
            }

            if (configUpdates.ContainsKey("displayName"))
            {
                setClauses.Add("display_name = @displayName");
                parameters["displayName"] = configUpdates["displayName"];
            }

            if (setClauses.Count > 0)
            {
                var sql = $"""
                    UPDATE season_tournaments
                    SET {string.Join(", ", setClauses)}, updated_at = NOW()
                    WHERE id = @id AND season_id = @seasonId
                    """;
                parameters["seasonId"] = seasonId;

                await conn.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
            }
        }

        await auditService.LogAsync(
            seasonId,
            actor.UserId,
            "tournament_updated",
            "season",
            seasonId,
            before: null,
            after: JsonSerializer.Serialize(new { bulk_config_applied_to = seasonTournamentIds, config = configUpdates }),
            reason: "Bulk configuration applied to season tournaments",
            ipAddress: actor.IpAddress,
            userAgent = actor.UserAgent,
            ct);
    }
}
