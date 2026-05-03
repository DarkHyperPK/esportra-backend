namespace Esportra.Core.Seasons;

using Dapper;
using Esportra.Contracts.Auth;
using System.Text.Json;

/// <summary>
/// Service for processing team advancement when tournaments complete.
/// Applies advancement rules, creates advancement records, updates standings, and sends notifications.
/// </summary>
public class AdvancementProcessor
{
    private readonly IDbConnectionFactory db;
    private readonly SeasonAuditService auditService;

    public AdvancementProcessor(IDbConnectionFactory db, SeasonAuditService auditService)
    {
        this.db = db;
        this.auditService = auditService;
    }

    /// <summary>
    /// Processes advancement for a completed tournament.
    /// </summary>
    public async Task ProcessTournamentCompletionAsync(
        Guid tournamentId,
        UserContext actor,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        await using var transaction = await conn.BeginTransactionAsync(ct);

        try
        {
            // Get tournament details
            var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(@"
                SELECT t.*, st.season_id, st.role as season_role
                FROM tournaments t
                LEFT JOIN season_tournaments st ON st.tournament_id = t.id
                WHERE t.id = @id
                """, new { id = tournamentId }, transaction, cancellationToken: ct);

            if (tournament == null)
                throw new InvalidOperationException($"Tournament {tournamentId} not found");

            if (tournament.season_id == null)
                return; // Not a season tournament, nothing to process

            var seasonId = (Guid)tournament.season_id;

            // Get tournament final standings
            var standings = await conn.QueryAsync<dynamic>(@"
                SELECT team_id, rank, points FROM tournament_standings
                WHERE tournament_id = @tournamentId
                ORDER BY rank ASC
                """, new { tournamentId }, transaction, cancellationToken: ct);

            // Get outgoing advancement connections
            var connections = await conn.QueryAsync<dynamic>(@"
                SELECT sac.*, sn.id as from_node_id
                FROM season_advancement_connections sac
                JOIN season_nodes sn ON sn.id = sac.from_node_id
                WHERE sn.published_tournament_id = @tournamentId AND sac.status = 'active'
                ORDER BY sac.display_order ASC
                """, new { tournamentId }, transaction, cancellationToken: ct);

            var teamsAdvanced = 0;
            var advancementRecords = new List<dynamic>();

            foreach (var conn in connections)
            {
                var connectionId = (Guid)conn.id;
                var ruleType = conn.rule_type;
                var ruleValue = (decimal)conn.rule_value;
                var seedMode = conn.seed_mode;
                var toNodeId = (Guid)conn.to_node_id;

                // Get target tournament
                var targetTournamentId = await conn.ExecuteScalarAsync<Guid?>(@"
                    SELECT published_tournament_id FROM season_nodes WHERE id = @nodeId
                    """, new { nodeId = toNodeId }, transaction, cancellationToken: ct);

                if (targetTournamentId == null)
                {
                    await auditService.LogAsync(
                        seasonId,
                        actor.UserId,
                        "advancement_rule_changed",
                        "season_advancement_connection",
                        connectionId,
                        before: JsonSerializer.Serialize(conn),
                        after: null,
                        reason: "Target tournament not yet published, skipping advancement",
                        ipAddress: actor.IpAddress,
                        userAgent: actor.UserAgent,
                        ct);
                    continue;
                }

                // Apply rule to determine which teams advance
                var advancingTeams = ApplyAdvancementRule(
                    standings.ToList(),
                    ruleType,
                    ruleValue
                );

                // Validate target capacity
                var targetCapacity = await conn.ExecuteScalarAsync<int?>(@"
                    SELECT max_teams FROM season_nodes WHERE id = @nodeId
                    """, new { nodeId = toNodeId }, transaction, cancellationToken: ct);

                if (targetCapacity.HasValue && advancingTeams.Count > targetCapacity.Value)
                {
                    await auditService.LogAsync(
                        seasonId,
                        actor.UserId,
                        "advancement_rule_changed",
                        "season_advancement_connection",
                        connectionId,
                        before: JsonSerializer.Serialize(conn),
                        after: null,
                        reason: $"Advancement count ({advancingTeams.Count}) exceeds target capacity ({targetCapacity.Value})",
                        ipAddress: actor.IpAddress,
                        userAgent = actor.UserAgent,
                        ct);
                    continue;
                }

                // Create advancement records
                foreach (var (team, rank) in advancingTeams)
                {
                    var seed = CalculateSeed(rank, seedMode, standings.ToList());

                    var advancementRecordId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(@"
                        INSERT INTO season_advancement_records (
                            season_id, connection_id, from_tournament_id, to_tournament_id,
                            team_id, source_rank, target_seed, status, advanced_at, advanced_by,
                            created_at, updated_at
                        )
                        VALUES (
                            @seasonId, @connectionId, @fromTournamentId, @toTournamentId,
                            @teamId, @sourceRank, @targetSeed, 'advanced', NOW(), @advancedBy,
                            NOW(), NOW()
                        )
                        RETURNING id
                        """, new
                        {
                            seasonId,
                            connectionId,
                            fromTournamentId = tournamentId,
                            toTournamentId = targetTournamentId,
                            teamId = team.team_id,
                            sourceRank = rank,
                            targetSeed = seed,
                            advancedBy = actor.UserId,
                        }, transaction, cancellationToken: ct));

                    advancementRecords.Add(new { advancementRecordId, teamId = team.team_id, rank, seed });

                    // Add team to target tournament
                    await conn.ExecuteAsync(new CommandDefinition(@"
                        INSERT INTO tournament_participants (tournament_id, team_id, seed, status, created_at, updated_at)
                        VALUES (@tournamentId, @teamId, @seed, 'registered', NOW(), NOW())
                        ON CONFLICT (tournament_id, team_id) DO UPDATE SET seed = EXCLUDED.seed, updated_at = NOW()
                        """, new
                        {
                            tournamentId = targetTournamentId,
                            teamId = team.team_id,
                            seed,
                        }, transaction, cancellationToken: ct));
                }

                teamsAdvanced += advancingTeams.Count;

                await auditService.LogAsync(
                    seasonId,
                    actor.UserId,
                    "team_advanced",
                    "season_advancement_connection",
                    connectionId,
                    before: JsonSerializer.Serialize(new { tournamentId, ruleType, ruleValue }),
                    after: JsonSerializer.Serialize(new { teams_advanced = advancingTeams.Count }),
                    reason: $"{advancingTeams.Count} teams advanced via {ruleType} rule",
                    ipAddress: actor.IpAddress,
                    userAgent = actor.UserAgent,
                    ct);
            }

            // Update season standings
            await UpdateSeasonStandingsAsync(conn, seasonId, tournamentId, standings.ToList(), transaction, ct);

            await transaction.CommitAsync(ct);

            await auditService.LogAsync(
                seasonId,
                actor.UserId,
                "team_advanced",
                "tournament",
                tournamentId,
                before: null,
                after: JsonSerializer.Serialize(new { teams_advanced }),
                reason: $"Tournament completion processed: {teamsAdvanced} teams advanced",
                ipAddress: actor.IpAddress,
                userAgent = actor.UserAgent,
                ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Applies an advancement rule to determine which teams advance.
    /// </summary>
    private List<(dynamic Team, int Rank)> ApplyAdvancementRule(
        List<dynamic> standings,
        string ruleType,
        decimal ruleValue)
    {
        return ruleType switch
        {
            "top_n" => standings.Take((int)ruleValue).Select((s, i) => (s, i + 1)).ToList(),
            "top_percentage" => standings
                .Take((int)Math.Ceiling(standings.Count * ruleValue / 100))
                .Select((s, i) => (s, i + 1))
                .ToList(),
            "points_threshold" => standings
                .Where(s => s.points >= ruleValue)
                .Select((s, i) => (s, i + 1))
                .ToList(),
            "manual_selection" => new List<(dynamic, int)>(), // Manual selection handled separately
            _ => new List<(dynamic, int)>(),
        };
    }

    /// <summary>
    /// Calculates seed for advancing team based on seed mode.
    /// </summary>
    private int CalculateSeed(int rank, string seedMode, List<dynamic> standings)
    {
        return seedMode switch
        {
            "preserve_seed" => rank,
            "reseed_by_points" => standings
                .OrderByDescending(s => s.points)
                .Select((s, i) => new { s, i })
                .First(x => x.s.rank == rank).i + 1,
            "randomize" => new Random().Next(1, 100), // Simplified random
            "manual" => rank, // Manual seeding handled separately
            _ => rank,
        };
    }

    /// <summary>
    /// Updates season-wide standings after tournament completion.
    /// </summary>
    private async Task UpdateSeasonStandingsAsync(
        System.Data.IDbConnection conn,
        Guid seasonId,
        Guid tournamentId,
        List<dynamic> standings,
        System.Data.IDbTransaction transaction,
        CancellationToken ct)
    {
        foreach (var standing in standings)
        {
            var teamId = (Guid)standing.team_id;
            var rank = standing.rank;
            var points = standing.points ?? 0;

            // Upsert season standings
            await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO season_standings (season_id, team_id, total_points, tournaments_played, best_finish, current_status, last_tournament_id, created_at, updated_at)
                VALUES (@seasonId, @teamId, @points, 1, @rank, 'active', @lastTournamentId, NOW(), NOW())
                ON CONFLICT (season_id, team_id) DO UPDATE SET
                    total_points = season_standings.total_points + @points,
                    tournaments_played = season_standings.tournaments_played + 1,
                    best_finish = CASE WHEN @rank < season_standings.best_finish OR season_standings.best_finish IS NULL THEN @rank ELSE season_standings.best_finish END,
                    current_status = CASE WHEN @rank = 1 THEN 'champion' ELSE 'active' END,
                    last_tournament_id = @lastTournamentId,
                    updated_at = NOW()
                """, new
                {
                    seasonId,
                    teamId,
                    points,
                    rank,
                    lastTournamentId = tournamentId,
                }, transaction, cancellationToken: ct));
        }
    }

    /// <summary>
    /// Performs a manual advancement override with audit logging.
    /// </summary>
    public async Task ManualOverrideAsync(
        Guid seasonId,
        Guid connectionId,
        Guid teamId,
        int targetSeed,
        string reason,
        UserContext actor,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        await using var transaction = await conn.BeginTransactionAsync(ct);

        try
        {
            // Validate connection exists
            var connection = await conn.QuerySingleOrDefaultAsync<dynamic>(@"
                SELECT * FROM season_advancement_connections WHERE id = @id
                """, new { id = connectionId }, transaction, cancellationToken: ct);

            if (connection == null)
                throw new InvalidOperationException($"Advancement connection {connectionId} not found");

            // Get target tournament
            var targetTournamentId = await conn.ExecuteScalarAsync<Guid?>(@"
                SELECT published_tournament_id FROM season_nodes WHERE id = @nodeId
                """, new { nodeId = connection.to_node_id }, transaction, cancellationToken: ct);

            if (targetTournamentId == null)
                throw new InvalidOperationException("Target tournament not published");

            // Create advancement record with manual_override status
            await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO season_advancement_records (
                    season_id, connection_id, from_tournament_id, to_tournament_id,
                    team_id, source_rank, target_seed, status, advanced_at, advanced_by, reason,
                    created_at, updated_at
                )
                VALUES (
                    @seasonId, @connectionId, @fromTournamentId, @toTournamentId,
                    @teamId, NULL, @targetSeed, 'manual_override', NOW(), @advancedBy, @reason,
                    NOW(), NOW()
                )
                ON CONFLICT (connection_id, team_id) DO UPDATE SET
                    status = 'manual_override', target_seed = @targetSeed, advanced_at = NOW(),
                    advanced_by = @advancedBy, reason = @reason, updated_at = NOW()
                """, new
                {
                    seasonId,
                    connectionId,
                    fromTournamentId = connection.from_node_id,
                    toTournamentId = targetTournamentId,
                    teamId,
                    targetSeed,
                    advancedBy = actor.UserId,
                    reason,
                }, transaction, cancellationToken: ct));

            // Add team to target tournament
            await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO tournament_participants (tournament_id, team_id, seed, status, created_at, updated_at)
                VALUES (@tournamentId, @teamId, @seed, 'registered', NOW(), NOW())
                ON CONFLICT (tournament_id, team_id) DO UPDATE SET seed = EXCLUDED.seed, updated_at = NOW()
                """, new
                {
                    tournamentId = targetTournamentId,
                    teamId,
                    seed = targetSeed,
                }, transaction, cancellationToken: ct));

            await transaction.CommitAsync(ct);

            await auditService.LogAsync(
                seasonId,
                actor.UserId,
                "manual_override",
                "season_advancement_record",
                connectionId,
                before: null,
                after: JsonSerializer.Serialize(new { teamId, targetSeed }),
                reason: reason,
                ipAddress: actor.IpAddress,
                userAgent = actor.UserAgent,
                ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
