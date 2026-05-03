namespace Esportra.Core.Seasons;

using Dapper;
using Esportra.Contracts.Auth;
using System.Text.Json;

/// <summary>
/// Response from season publish operation.
/// </summary>
public record PublishSeasonResponse(
    bool Success,
    Guid SeasonId,
    string SeasonStatus,
    int TournamentsCreated,
    int TournamentsLinked,
    int ConnectionsWired,
    List<PublishedTournamentDto> Tournaments,
    List<string> Warnings
);

/// <summary>
/// DTO for a published tournament.
/// </summary>
public record PublishedTournamentDto(
    Guid NodeId,
    string NodeName,
    Guid TournamentId,
    string Slug,
    bool Created
);

/// <summary>
/// Service for executing atomic season publish transactions.
/// Creates real tournaments, season_tournament mappings, and advancement connections in a single transaction.
/// </summary>
public class PublishTransactionService
{
    private readonly IDbConnectionFactory db;
    private readonly AdvancementGraphValidator graphValidator;
    private readonly SeasonAuditService auditService;

    public PublishTransactionService(
        IDbConnectionFactory db,
        AdvancementGraphValidator graphValidator,
        SeasonAuditService auditService)
    {
        this.db = db;
        this.graphValidator = graphValidator;
        this.auditService = auditService;
    }

    /// <summary>
    /// Publishes a season atomically with idempotency support.
    /// </summary>
    public async Task<PublishSeasonResponse> PublishSeasonAsync(
        Guid seasonId,
        bool allowIncomplete,
        bool activate,
        string? idempotencyKey,
        UserContext actor,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        await using var transaction = await conn.BeginTransactionAsync(ct);

        try
        {
            // Check idempotency if key provided
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                var existingResponse = await CheckIdempotencyAsync(conn, idempotencyKey, ct);
                if (existingResponse != null)
                {
                    await transaction.CommitAsync(ct);
                    return existingResponse;
                }
            }

            // 1. Validate season essentials
            var season = await conn.QuerySingleOrDefaultAsync<dynamic>(@"
                SELECT * FROM seasons WHERE id = @id AND deleted_at IS NULL
                """, new { id = seasonId }, transaction, cancellationToken: ct);

            if (season == null)
                throw new InvalidOperationException($"Season {seasonId} not found");

            if (season.status != "draft")
                throw new InvalidOperationException($"Season must be in draft status to publish (current: {season.status})");

            // 2. Validate tournament configs
            var nodes = await conn.QueryAsync<dynamic>(@"
                SELECT * FROM season_nodes WHERE season_id = @seasonId
                """, new { seasonId }, transaction, cancellationToken: ct);

            var configWarnings = new List<string>();
            foreach (var node in nodes)
            {
                if (!allowIncomplete)
                {
                    if (node.tournament_format == null)
                        configWarnings.Add($"Node '{node.name}' is missing tournament format");

                    if (node.team_size == null)
                        configWarnings.Add($"Node '{node.name}' is missing team size");

                    if (node.max_teams == null)
                        configWarnings.Add($"Node '{node.name}' is missing max teams");

                    if (node.registration_type == null)
                        configWarnings.Add($"Node '{node.name}' is missing registration type");
                }
            }

            if (configWarnings.Count > 0 && !allowIncomplete)
            {
                throw new InvalidOperationException($"Tournament configurations incomplete: {string.Join("; ", configWarnings)}");
            }

            // 3. Validate advancement graph
            var graphValidation = await graphValidator.ValidateGraphAsync(seasonId, ct);
            if (!graphValidation.IsValid)
            {
                throw new InvalidOperationException($"Advancement graph validation failed: {string.Join("; ", graphValidation.Errors)}");
            }

            configWarnings.AddRange(graphValidation.Warnings);

            // 4. Create real tournament records
            var tournamentsCreated = new List<PublishedTournamentDto>();
            var tournamentsLinked = new List<PublishedTournamentDto>();

            foreach (var node in nodes)
            {
                // Skip nodes that already have published tournaments
                if (node.published_tournament_id != null)
                {
                    tournamentsLinked.Add(new PublishedTournamentDto(
                        NodeId: (Guid)node.id,
                        NodeName: node.name,
                        TournamentId: (Guid)node.published_tournament_id,
                        Slug: node.slug ?? "",
                        Created: false
                    ));
                    continue;
                }

                // Skip root nodes (they don't represent actual tournaments)
                if (node.node_type == "root")
                    continue;

                // Create tournament
                var tournamentId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(@"
                    INSERT INTO tournaments (
                        name, slug, game, format, team_size, max_teams, min_teams,
                        best_of, registration_type, entry_fee, prize_pool,
                        check_in_minutes_before, registration_opens_at,
                        season_id, season_role, created_via, status, organizer_id
                    )
                    VALUES (
                        @name, @slug, @game, @format, @teamSize, @maxTeams, @minTeams,
                        @bestOf, @registrationType, @entryFee, @prizePool,
                        @checkInMinutesBefore, @registrationOpensAt,
                        @seasonId, @seasonRole, 'season', 'draft', @organizerId
                    )
                    RETURNING id
                    """, new
                    {
                        name = node.name,
                        slug = node.slug ?? $"{seasonId.ToString()[..8]}-{node.id.ToString()[..8]}",
                        game = season.game,
                        format = node.tournament_format,
                        teamSize = node.team_size,
                        maxTeams = node.max_teams,
                        minTeams = node.min_teams,
                        bestOf = node.best_of,
                        registrationType = node.registration_type,
                        entryFee = node.entry_fee,
                        prizePool = node.prize_pool,
                        checkInMinutesBefore = node.check_in_minutes_before,
                        registrationOpensAt = node.registration_opens_at,
                        seasonId,
                        seasonRole = node.node_type,
                        organizerId = season.owner_user_id,
                    }, transaction, cancellationToken: ct));

                // Update node with published tournament reference
                await conn.ExecuteAsync(new CommandDefinition(@"
                    UPDATE season_nodes
                    SET published_tournament_id = @tournamentId, updated_at = NOW()
                    WHERE id = @nodeId
                    """, new { tournamentId, nodeId = node.id }, transaction, cancellationToken: ct);

                // Create season_tournament mapping
                var seasonTournamentId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(@"
                    INSERT INTO season_tournaments (
                        season_id, tournament_id, role, display_name, sort_order, status, created_at, updated_at
                    )
                    VALUES (@seasonId, @tournamentId, @role, @displayName, @displayOrder, 'draft', NOW(), NOW())
                    RETURNING id
                    """, new
                    {
                        seasonId,
                        tournamentId,
                        role = node.node_type,
                        displayName = node.name,
                        displayOrder = node.display_order,
                    }, transaction, cancellationToken: ct));

                tournamentsCreated.Add(new PublishedTournamentDto(
                    NodeId: (Guid)node.id,
                    NodeName: node.name,
                    TournamentId: tournamentId,
                    Slug = node.slug ?? "",
                    Created: true
                ));
            }

            // 5. Count advancement connections
            var connectionsWired = await conn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM season_advancement_connections WHERE season_id = @seasonId
                """, new { seasonId }, transaction, cancellationToken: ct);

            // 6. Update season status
            var newStatus = activate ? "active" : "published";
            var timestampColumn = activate ? "published_at" : "published_at";

            await conn.ExecuteAsync(new CommandDefinition($@"
                UPDATE seasons
                SET status = @status, {timestampColumn} = NOW(), updated_at = NOW()
                WHERE id = @id
                """, new { status = newStatus, id = seasonId }, transaction, cancellationToken: ct));

            // 7. Write audit log
            await auditService.LogAsync(
                seasonId,
                actor.UserId,
                "season_published",
                "season",
                seasonId,
                before: JsonSerializer.Serialize(new { status = "draft" }),
                after: JsonSerializer.Serialize(new { status = newStatus, tournaments_created = tournamentsCreated.Count }),
                reason: "Season published with atomic transaction",
                ipAddress: actor.IpAddress,
                userAgent: actor.UserAgent,
                ct);

            // 8. Store idempotency response if key provided
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                await StoreIdempotencyAsync(conn, idempotencyKey, new PublishSeasonResponse(
                    Success: true,
                    SeasonId: seasonId,
                    SeasonStatus: newStatus,
                    TournamentsCreated: tournamentsCreated.Count,
                    TournamentsLinked: tournamentsLinked.Count,
                    ConnectionsWired: connectionsWired,
                    Tournaments: tournamentsCreated.Concat(tournamentsLinked).ToList(),
                    Warnings: configWarnings
                ), ct);
            }

            await transaction.CommitAsync(ct);

            return new PublishSeasonResponse(
                Success: true,
                SeasonId: seasonId,
                SeasonStatus: newStatus,
                TournamentsCreated: tournamentsCreated.Count,
                TournamentsLinked: tournamentsLinked.Count,
                ConnectionsWired: connectionsWired,
                Tournaments: tournamentsCreated.Concat(tournamentsLinked).ToList(),
                Warnings: configWarnings
            );
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Checks if an idempotency key has already been used.
    /// </summary>
    private async Task<PublishSeasonResponse?> CheckIdempotencyAsync(
        System.Data.IDbConnection conn,
        string idempotencyKey,
        CancellationToken ct)
    {
        // Check if idempotency table exists, if not return null (first run)
        var tableExists = await conn.ExecuteScalarAsync<bool>(@"
            SELECT EXISTS (
                SELECT FROM information_schema.tables
                WHERE table_name = 'idempotency_keys'
            )
        ", cancellationToken: ct);

        if (!tableExists)
            return null;

        var responseJson = await conn.QuerySingleOrDefaultAsync<string?>(@"
            SELECT response FROM idempotency_keys WHERE key = @key AND created_at > NOW() - INTERVAL '7 days'
        ", new { key = idempotencyKey }, cancellationToken: ct);

        if (responseJson == null)
            return null;

        return JsonSerializer.Deserialize<PublishSeasonResponse>(responseJson);
    }

    /// <summary>
    /// Stores an idempotency response.
    /// </summary>
    private async Task StoreIdempotencyAsync(
        System.Data.IDbConnection conn,
        string idempotencyKey,
        PublishSeasonResponse response,
        CancellationToken ct)
    {
        // Create idempotency table if it doesn't exist
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS idempotency_keys (
                key TEXT PRIMARY KEY,
                response JSONB NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            )
        ", cancellationToken: ct);

        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO idempotency_keys (key, response, created_at)
            VALUES (@key, @response::jsonb, NOW())
            ON CONFLICT (key) DO UPDATE SET response = EXCLUDED.response, created_at = NOW()
        ", new
        {
            key = idempotencyKey,
            response = JsonSerializer.Serialize(response),
        }, cancellationToken: ct);
    }
}
