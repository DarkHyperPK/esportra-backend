namespace Esportra.Core.Seasons;

using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;

/// <summary>
/// Service for managing season lifecycle operations with audit logging.
/// </summary>
public class SeasonService
{
    private readonly IDbConnectionFactory db;
    private readonly AuditService auditService;

    public SeasonService(IDbConnectionFactory db, AuditService auditService)
    {
        this.db = db;
        this.auditService = auditService;
    }

    /// <summary>
    /// Creates a new season in draft status.
    /// </summary>
    public async Task<Guid> CreateDraftAsync(
        string name,
        string slug,
        string game,
        string participantMode,
        Guid ownerId,
        Guid? organizationId,
        string? description,
        bool isPublic,
        bool allowManualOverrides,
        string? startDate,
        string? endDate,
        string? visibility,
        UserContext actor,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var seasonId = Guid.NewGuid();

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO seasons (
                id, name, slug, description, game, participant_mode, status,
                owner_user_id, organization_id, is_public, allow_manual_overrides,
                start_date, end_date, visibility, created_by, created_at, updated_at
            )
            VALUES (
                @id, @name, @slug, @description, @game, @participantMode, 'draft',
                @ownerId, @organizationId, @isPublic, @allowManualOverrides,
                @startDate, @endDate, @visibility, @actorId, NOW(), NOW()
            )
            """, new
            {
                id = seasonId,
                name,
                slug,
                description,
                game,
                participantMode,
                ownerId,
                organizationId,
                isPublic,
                allowManualOverrides,
                startDate = startDate != null ? DateTime.Parse(startDate) : null,
                endDate = endDate != null ? DateTime.Parse(endDate) : null,
                visibility = visibility ?? "private",
                actorId = actor.UserId,
            }, cancellationToken: ct));

        await auditService.LogAsync(
            seasonId,
            actor.UserId,
            "season_created",
            "season",
            seasonId,
            before: null,
            after: JsonSerializer.Serialize(new { name, slug, game, status = "draft" }),
            reason: "Season created",
            ipAddress: actor.IpAddress,
            userAgent: actor.UserAgent,
            ct);

        return seasonId;
    }

    /// <summary>
    /// Updates season essentials with version increment and audit logging.
    /// </summary>
    public async Task UpdateAsync(
        Guid seasonId,
        string? name,
        string? slug,
        string? description,
        string? game,
        string? participantMode,
        bool? isPublic,
        bool? allowManualOverrides,
        string? startDate,
        string? endDate,
        string? visibility,
        UserContext actor,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        // Fetch current state for audit
        var current = await conn.QuerySingleOrDefaultAsync<dynamic>(@"
            SELECT * FROM seasons WHERE id = @id", new { id = seasonId }, cancellationToken: ct);

        if (current == null)
            throw new InvalidOperationException($"Season {seasonId} not found");

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE seasons
            SET name = COALESCE(@name, name),
                slug = COALESCE(@slug, slug),
                description = @description,
                game = COALESCE(@game, game),
                participant_mode = COALESCE(@participantMode, participant_mode),
                is_public = COALESCE(@isPublic, is_public),
                allow_manual_overrides = COALESCE(@allowManualOverrides, allow_manual_overrides),
                start_date = @startDate,
                end_date = @endDate,
                visibility = COALESCE(@visibility, visibility),
                version = version + 1,
                updated_at = NOW()
            WHERE id = @id
            """, new
            {
                id = seasonId,
                name,
                slug,
                description,
                game,
                participantMode,
                isPublic,
                allowManualOverrides,
                startDate = startDate != null ? DateTime.Parse(startDate) : null,
                endDate = endDate != null ? DateTime.Parse(endDate) : null,
                visibility,
            }, cancellationToken: ct));

        await auditService.LogAsync(
            seasonId,
            actor.UserId,
            "season_updated",
            "season",
            seasonId,
            before: JsonSerializer.Serialize(current),
            after: JsonSerializer.Serialize(new { name, slug, description, game, isPublic }),
            reason: "Season details updated",
            ipAddress: actor.IpAddress,
            userAgent = actor.UserAgent,
            ct);
    }

    /// <summary>
    /// Soft deletes a season by setting deleted_at timestamp.
    /// </summary>
    public async Task DeleteAsync(Guid seasonId, UserContext actor, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var current = await conn.QuerySingleOrDefaultAsync<dynamic>(@"
            SELECT * FROM seasons WHERE id = @id", new { id = seasonId }, cancellationToken: ct);

        if (current == null)
            throw new InvalidOperationException($"Season {seasonId} not found");

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE seasons
            SET deleted_at = NOW(), updated_at = NOW()
            WHERE id = @id AND deleted_at IS NULL
            """, new { id = seasonId }, cancellationToken: ct));

        await auditService.LogAsync(
            seasonId,
            actor.UserId,
            "season_deleted",
            "season",
            seasonId,
            before: JsonSerializer.Serialize(current),
            after: null,
            reason: "Season soft deleted",
            ipAddress: actor.IpAddress,
            userAgent = actor.UserAgent,
            ct);
    }

    /// <summary>
    /// Archives a completed season.
    /// </summary>
    public async Task ArchiveAsync(Guid seasonId, UserContext actor, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var current = await conn.QuerySingleOrDefaultAsync<dynamic>(@"
            SELECT * FROM seasons WHERE id = @id", new { id = seasonId }, cancellationToken: ct);

        if (current == null)
            throw new InvalidOperationException($"Season {seasonId} not found");

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE seasons
            SET status = 'archived', archived_at = NOW(), updated_at = NOW()
            WHERE id = @id AND status = 'completed'
            """, new { id = seasonId }, cancellationToken: ct));

        await auditService.LogAsync(
            seasonId,
            actor.UserId,
            "season_archived",
            "season",
            seasonId,
            before: JsonSerializer.Serialize(current),
            after: JsonSerializer.Serialize(new { status = "archived", archived_at = DateTime.UtcNow }),
            reason: "Season archived after completion",
            ipAddress: actor.IpAddress,
            userAgent = actor.UserAgent,
            ct);
    }

    /// <summary>
    /// Cancels an active season.
    /// </summary>
    public async Task CancelAsync(Guid seasonId, string reason, UserContext actor, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var current = await conn.QuerySingleOrDefaultAsync<dynamic>(@"
            SELECT * FROM seasons WHERE id = @id", new { id = seasonId }, cancellationToken: ct);

        if (current == null)
            throw new InvalidOperationException($"Season {seasonId} not found");

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE seasons
            SET status = 'cancelled', cancelled_at = NOW(), updated_at = NOW()
            WHERE id = @id AND status IN ('draft', 'published', 'active')
            """, new { id = seasonId }, cancellationToken: ct));

        await auditService.LogAsync(
            seasonId,
            actor.UserId,
            "season_cancelled",
            "season",
            seasonId,
            before: JsonSerializer.Serialize(current),
            after: JsonSerializer.Serialize(new { status = "cancelled", cancelled_at = DateTime.UtcNow }),
            reason: reason,
            ipAddress: actor.IpAddress,
            userAgent = actor.UserAgent,
            ct);
    }

    /// <summary>
    /// Duplicates a season structure (creates new season with same nodes/tournaments).
    /// </summary>
    public async Task<Guid> DuplicateAsync(
        Guid sourceSeasonId,
        string newName,
        string newSlug,
        UserContext actor,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        await using var transaction = await conn.BeginTransactionAsync(ct);

        try
        {
            // Fetch source season
            var source = await conn.QuerySingleOrDefaultAsync<dynamic>(@"
                SELECT * FROM seasons WHERE id = @id", new { id = sourceSeasonId }, transaction, cancellationToken: ct);

            if (source == null)
                throw new InvalidOperationException($"Season {sourceSeasonId} not found");

            // Create new season
            var newSeasonId = Guid.NewGuid();
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO seasons (
                    id, name, slug, description, game, participant_mode, status,
                    owner_user_id, organization_id, is_public, allow_manual_overrides,
                    start_date, end_date, visibility, created_by, created_at, updated_at
                )
                SELECT
                    @newId, @newName, @newSlug, description, game, participant_mode, 'draft',
                    owner_user_id, organization_id, is_public, allow_manual_overrides,
                    start_date, end_date, visibility, @actorId, NOW(), NOW()
                FROM seasons
                WHERE id = @sourceId
                """, new
                {
                    newId = newSeasonId,
                    newName,
                    newSlug,
                    actorId = actor.UserId,
                    sourceId = sourceSeasonId,
                }, transaction, cancellationToken: ct));

            // Copy season nodes
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO season_nodes (
                    id, season_id, parent_node_id, name, slug, node_type, display_order,
                    status, region, city, country, linked_tournament_id, linked_stage_id,
                    registration_deadline, starts_at, ends_at, metadata,
                    tournament_format, team_size, max_teams, min_teams, best_of,
                    registration_type, entry_fee, prize_pool, check_in_minutes_before,
                    registration_opens_at, published_tournament_id, created_at, updated_at
                )
                SELECT
                    gen_random_uuid(), @newSeasonId, parent_node_id, name, @newSlugPrefix || slug,
                    node_type, display_order, status, region, city, country,
                    linked_tournament_id, linked_stage_id, registration_deadline,
                    starts_at, ends_at, metadata, tournament_format, team_size,
                    max_teams, min_teams, best_of, registration_type, entry_fee,
                    prize_pool, check_in_minutes_before, registration_opens_at,
                    published_tournament_id, NOW(), NOW()
                FROM season_nodes
                WHERE season_id = @sourceId
                """, new
                {
                    newSeasonId,
                    newSlugPrefix = $"{newSlug}-",
                    sourceId = sourceSeasonId,
                }, transaction, cancellationToken: ct);

            // Copy advancement connections
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO season_advancement_connections (
                    id, season_id, from_node_id, to_node_id, rule_type, rule_value,
                    seed_mode, label, display_order, metadata, created_at, updated_at
                )
                SELECT
                    gen_random_uuid(), @newSeasonId,
                    (SELECT id FROM season_nodes WHERE season_id = @newSeasonId AND name = (SELECT name FROM season_nodes WHERE id = sac.from_node_id)),
                    (SELECT id FROM season_nodes WHERE season_id = @newSeasonId AND name = (SELECT name FROM season_nodes WHERE id = sac.to_node_id)),
                    rule_type, rule_value, seed_mode, label, display_order, metadata, NOW(), NOW()
                FROM season_advancement_connections sac
                WHERE sac.season_id = @sourceId
                """, new
                {
                    newSeasonId,
                    sourceId = sourceSeasonId,
                }, transaction, cancellationToken: ct);

            await transaction.CommitAsync(ct);

            await auditService.LogAsync(
                newSeasonId,
                actor.UserId,
                "season_created",
                "season",
                newSeasonId,
                before: null,
                after: JsonSerializer.Serialize(new { name = newName, slug = newSlug, source_season_id = sourceSeasonId }),
                reason: $"Season duplicated from {sourceSeasonId}",
                ipAddress: actor.IpAddress,
                userAgent = actor.UserAgent,
                ct);

            return newSeasonId;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
