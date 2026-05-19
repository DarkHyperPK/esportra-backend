using System.Data;
using Dapper;
using Esportra.Contracts.Database;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Services;

/// <summary>
/// Engine that computes and applies team advancement across a season's tournament circuit.
/// Reads season_advancement_rules + season_standings to determine which teams move
/// from source tournaments/nodes to target tournaments/nodes.
/// </summary>
public sealed class SeasonAdvancementService(
    IDbConnectionFactory db,
    SeasonStandingsSyncService standingsSync,
    ILogger<SeasonAdvancementService> logger)
{
    // ── Preview ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Computes which teams would advance based on current standings + advancement rules,
    /// without actually applying anything. Returns a list of proposed movements.
    /// Optionally scoped to a single source tournament.
    /// </summary>
    public async Task<AdvancementPreviewResult> PreviewAdvancementAsync(
        Guid seasonId, Guid? sourceTournamentId = null, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        return await PreviewAdvancementAsync(seasonId, sourceTournamentId, conn, ct);
    }

    public async Task<AdvancementPreviewResult> PreviewAdvancementAsync(
        Guid seasonId, Guid? sourceTournamentId, IDbConnection conn, CancellationToken ct = default)
    {
        // 1. Load advancement rules (optionally filtered by source tournament)
        var rules = (await conn.QueryAsync<AdvancementRuleRow>(
            """
            SELECT ar.id, ar.season_id, ar.source_tournament_id, ar.target_tournament_id,
                   ar.source_node_id, ar.target_node_id,
                   ar.placement_start, ar.placement_end, ar.advancement_count, ar.seed_mode,
                   sn_src.name AS source_node_name, sn_tgt.name AS target_node_name,
                   t_src.name AS source_tournament_name, t_tgt.name AS target_tournament_name,
                   t_tgt.status AS target_tournament_status
            FROM public.season_advancement_rules ar
            LEFT JOIN public.season_nodes sn_src ON sn_src.id = ar.source_node_id
            LEFT JOIN public.season_nodes sn_tgt ON sn_tgt.id = ar.target_node_id
            LEFT JOIN public.tournaments t_src ON t_src.id = ar.source_tournament_id
            LEFT JOIN public.tournaments t_tgt ON t_tgt.id = ar.target_tournament_id
            WHERE ar.season_id = @seasonId
              AND (@sourceTournamentId IS NULL OR ar.source_tournament_id = @sourceTournamentId)
            ORDER BY ar.placement_start ASC
            """,
            new { seasonId, sourceTournamentId })).AsList();

        if (rules.Count == 0)
            return new AdvancementPreviewResult([], [], "No advancement rules configured for this season.");

        // 2. Load current standings (ranked)
        var standings = (await conn.QueryAsync<StandingRow>(
            """
            SELECT ss.team_id, ss.total_points, ss.standing_rank, ss.qualification_status,
                   t.name AS team_name, t.logo_url AS team_logo_url
            FROM public.season_standings ss
            LEFT JOIN public.teams t ON t.id = ss.team_id
            WHERE ss.season_id = @seasonId
            ORDER BY ss.standing_rank NULLS LAST, ss.total_points DESC
            """,
            new { seasonId })).AsList();

        if (standings.Count == 0)
            return new AdvancementPreviewResult([], [], "No standings data available. Run standings recalculation first.");

        // 3. Tournament-scoped rules use source tournament placements; standings-only
        // rules use season-wide rank. That keeps qualifier-to-final and
        // points-events-to-final explicit instead of blending both semantics.
        var tournamentPlacements = new Dictionary<Guid, List<StandingRow>>();
        var sourceTournamentIds = rules
            .Where(r => r.SourceTournamentId.HasValue)
            .Select(r => r.SourceTournamentId!.Value)
            .Distinct()
            .ToList();

        foreach (var tid in sourceTournamentIds)
        {
            var placements = await standingsSync.ResolveTournamentPlacementsAsync(conn, tid);
            var teamIds = placements.Select(p => p.TeamId).Distinct().ToArray();
            var teams = teamIds.Length == 0
                ? new Dictionary<Guid, TeamLookupRow>()
                : (await conn.QueryAsync<TeamLookupRow>(
                """
                SELECT id, name, logo_url AS logoUrl
                FROM public.teams
                WHERE id = ANY(@teamIds)
                """,
                new { teamIds })).ToDictionary(t => t.Id);

            tournamentPlacements[tid] = placements
                .OrderBy(p => p.Placement)
                .Select(p =>
                {
                    teams.TryGetValue(p.TeamId, out var team);
                    return new StandingRow(p.TeamId, 0, p.Placement, null, team?.Name, team?.LogoUrl);
                })
                .ToList();
        }

        // 4. Compute proposed movements
        var movements = new List<ProposedMovement>();
        var warnings = new List<string>();
        var alreadyAdvanced = new HashSet<Guid>();

        // Check which teams are already registered in target tournaments
        var targetTournamentIds = rules
            .Where(r => r.TargetTournamentId.HasValue)
            .Select(r => r.TargetTournamentId!.Value)
            .Distinct()
            .ToArray();

        var existingRegistrations = new HashSet<(Guid tournamentId, Guid teamId)>();
        if (targetTournamentIds.Length > 0)
        {
            var existing = await conn.QueryAsync<dynamic>(
                """
                SELECT tournament_id, team_id
                FROM public.tournament_participants
                WHERE tournament_id = ANY(@targetTournamentIds)
                  AND team_id IS NOT NULL
                  AND status NOT IN ('rejected', 'cancelled')
                """,
                new { targetTournamentIds });
            foreach (var row in existing)
                existingRegistrations.Add(((Guid)row.tournament_id, (Guid)row.team_id));
        }

        foreach (var rule in rules)
        {
            var sourcePlacements = rule.SourceTournamentId.HasValue && tournamentPlacements.TryGetValue(rule.SourceTournamentId.Value, out var placements)
                ? placements
                : standings;

            var eligibleTeams = sourcePlacements
                .Where(s => s.StandingRank.HasValue
                    && s.StandingRank.Value >= rule.PlacementStart
                    && s.StandingRank.Value <= rule.PlacementEnd
                    && !alreadyAdvanced.Contains(s.TeamId))
                .OrderBy(s => s.StandingRank)
                .Take(rule.AdvancementCount)
                .ToList();

            if (eligibleTeams.Count == 0)
            {
                warnings.Add($"Rule '{rule.SourceNodeName ?? "?"} → {rule.TargetNodeName ?? "?"}' (places {rule.PlacementStart}-{rule.PlacementEnd}): no eligible teams found.");
                continue;
            }

            if (eligibleTeams.Count < rule.AdvancementCount)
            {
                warnings.Add($"Rule '{rule.SourceNodeName ?? "?"} → {rule.TargetNodeName ?? "?"}': only {eligibleTeams.Count} of {rule.AdvancementCount} teams available.");
            }

            // Check target tournament status
            if (rule.TargetTournamentId.HasValue && rule.TargetTournamentStatus is "completed" or "cancelled")
            {
                warnings.Add($"Target tournament '{rule.TargetTournamentName}' is already {rule.TargetTournamentStatus}. Teams cannot be registered.");
                continue;
            }

            foreach (var team in eligibleTeams)
            {
                bool alreadyRegistered = rule.TargetTournamentId.HasValue
                    && existingRegistrations.Contains((rule.TargetTournamentId.Value, team.TeamId));

                movements.Add(new ProposedMovement(
                    TeamId: team.TeamId,
                    TeamName: team.TeamName,
                    TeamLogoUrl: team.TeamLogoUrl,
                    CurrentStandingRank: team.StandingRank ?? 0,
                    TotalPoints: team.TotalPoints,
                    SourceNodeId: rule.SourceNodeId,
                    SourceNodeName: rule.SourceNodeName ?? rule.SourceTournamentName,
                    TargetNodeId: rule.TargetNodeId,
                    TargetNodeName: rule.TargetNodeName ?? rule.TargetTournamentName,
                    TargetTournamentId: rule.TargetTournamentId,
                    RuleId: rule.Id,
                    SeedMode: rule.SeedMode,
                    AlreadyRegistered: alreadyRegistered));

                alreadyAdvanced.Add(team.TeamId);
            }
        }

        return new AdvancementPreviewResult(movements, warnings, null);
    }

    // ── Apply ────────────────────────────────────────────────────────────────
    /// <summary>
    /// Applies advancement: registers qualifying teams in target tournaments,
    /// creates qualification records, and optionally updates season_participants.
    /// Supports constrained organizer overrides via explicit team list.
    /// </summary>
    public async Task<AdvancementApplyResult> ApplyAdvancementAsync(
        Guid seasonId, Guid? sourceTournamentId = null,
        List<AdvancementOverride>? overrides = null,
        HybridCache? cache = null, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();
        var result = await ApplyAdvancementAsync(seasonId, sourceTournamentId, overrides, conn, tx, cache, ct);
        tx.Commit();
        return result;
    }

    public async Task<AdvancementApplyResult> ApplyAdvancementAsync(
        Guid seasonId, Guid? sourceTournamentId,
        List<AdvancementOverride>? overrides,
        IDbConnection conn, IDbTransaction tx,
        HybridCache? cache = null, CancellationToken ct = default)
    {
        // First get the preview to know what should advance
        var preview = await PreviewAdvancementAsync(seasonId, sourceTournamentId, conn, ct);

        if (preview.Movements.Count == 0)
            return new AdvancementApplyResult(0, 0, preview.Warnings, "No teams eligible for advancement.");

        // Apply overrides: organizer can exclude teams or add extras (within rules)
        var finalMovements = preview.Movements.ToList();
        if (overrides is not null)
        {
            // Remove excluded teams
            var excluded = overrides.Where(o => o.Action == "exclude").Select(o => o.TeamId).ToHashSet();
            finalMovements.RemoveAll(m => excluded.Contains(m.TeamId));
        }

        int advancedCount = 0;
        int qualificationCount = 0;
        var warnings = preview.Warnings.ToList();

        foreach (var movement in finalMovements)
        {
            if (movement.AlreadyRegistered)
            {
                warnings.Add($"Team '{movement.TeamName}' is already registered in target tournament. Skipped.");
                continue;
            }

            // Register team in target tournament
            if (movement.TargetTournamentId.HasValue)
            {
                try
                {
                    // Get team info for registration
                    var teamInfo = await conn.QuerySingleOrDefaultAsync<dynamic>(
                        "SELECT id, name, logo_url FROM public.teams WHERE id = @teamId",
                        new { teamId = movement.TeamId }, tx);

                    if (teamInfo is not null)
                    {
                        var inserted = await conn.ExecuteAsync(
                            """
                            INSERT INTO public.tournament_participants
                                (tournament_id, team_id, team_name, status, participant_type, source, is_mock)
                            VALUES
                                (@tournamentId, @teamId, @teamName, 'approved', 'team', 'advancement', FALSE)
                            ON CONFLICT (tournament_id, COALESCE(team_id, '00000000-0000-0000-0000-000000000000'::uuid))
                            DO NOTHING
                            """,
                            new { tournamentId = movement.TargetTournamentId.Value, teamId = movement.TeamId, teamName = (string?)teamInfo.name }, tx);

                        if (inserted > 0) advancedCount++;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[Advancement] Failed to register team {TeamId} in tournament {TournamentId}.",
                        movement.TeamId, movement.TargetTournamentId);
                    warnings.Add($"Failed to register team '{movement.TeamName}': {ex.Message}");
                }
            }

            // Create qualification record
            try
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO public.season_qualification_records
                        (season_id, source_node_id, destination_node_id, team_id, status, qualification_type, display_name, notes)
                    VALUES
                        (@seasonId, @sourceNodeId, @destinationNodeId, @teamId, 'qualified', 'qualified',
                         @displayName, @notes)
                    ON CONFLICT DO NOTHING
                    """,
                    new
                    {
                        seasonId,
                        sourceNodeId = movement.SourceNodeId,
                        destinationNodeId = movement.TargetNodeId,
                        teamId = movement.TeamId,
                        displayName = movement.TeamName,
                        notes = $"Auto-advanced from {movement.SourceNodeName} (rank #{movement.CurrentStandingRank})"
                    }, tx);

                qualificationCount++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Advancement] Failed to create qualification record for team {TeamId}.", movement.TeamId);
            }

            // Update season_standings qualification_status
            await conn.ExecuteAsync(
                """
                UPDATE public.season_standings
                SET qualification_status = 'qualified', version = version + 1, updated_at = NOW()
                WHERE season_id = @seasonId AND team_id = @teamId
                """,
                new { seasonId, teamId = movement.TeamId }, tx);
        }

        // Invalidate caches
        if (cache is not null)
        {
            try { await cache.RemoveAsync($"season:{seasonId}", ct); } catch { }
            try { await cache.RemoveAsync($"season-detail:{seasonId}", ct); } catch { }
            try { await cache.RemoveByTagAsync("tournament-list", ct); } catch { }
        }

        logger.LogInformation(
            "[Advancement] Season {SeasonId}: {AdvancedCount} teams advanced, {QualificationCount} qualification records created.",
            seasonId, advancedCount, qualificationCount);

        return new AdvancementApplyResult(advancedCount, qualificationCount, warnings, null);
    }

    // ── Get Advancement Connections ──────────────────────────────────────────
    /// <summary>
    /// Returns the advancement connections (rules joined with node names) for display.
    /// </summary>
    public async Task<List<AdvancementConnectionRow>> GetAdvancementConnectionsAsync(Guid seasonId)
    {
        using var conn = db.CreateConnection();

        return (await conn.QueryAsync<AdvancementConnectionRow>(
            """
            SELECT ar.id, ar.season_id,
                   COALESCE(ar.source_node_id, sn_src_t.id) AS source_node_id,
                   COALESCE(sn_src.name, t_src.name, 'Unknown') AS source_node_name,
                   COALESCE(ar.target_node_id, sn_tgt_t.id) AS target_node_id,
                   COALESCE(sn_tgt.name, t_tgt.name, 'Unknown') AS target_node_name,
                   ar.placement_start, ar.placement_end, ar.advancement_count
            FROM public.season_advancement_rules ar
            LEFT JOIN public.season_nodes sn_src ON sn_src.id = ar.source_node_id
            LEFT JOIN public.season_nodes sn_tgt ON sn_tgt.id = ar.target_node_id
            LEFT JOIN public.tournaments t_src ON t_src.id = ar.source_tournament_id
            LEFT JOIN public.tournaments t_tgt ON t_tgt.id = ar.target_tournament_id
            LEFT JOIN public.season_nodes sn_src_t ON sn_src_t.season_id = ar.season_id AND sn_src_t.linked_tournament_id = ar.source_tournament_id
            LEFT JOIN public.season_nodes sn_tgt_t ON sn_tgt_t.season_id = ar.season_id AND sn_tgt_t.linked_tournament_id = ar.target_tournament_id
            WHERE ar.season_id = @seasonId
            ORDER BY ar.placement_start ASC, ar.created_at ASC
            """,
            new { seasonId })).AsList();
    }

    // ── DTOs ─────────────────────────────────────────────────────────────────

    private sealed record AdvancementRuleRow(
        Guid Id, Guid SeasonId, Guid? SourceTournamentId, Guid? TargetTournamentId,
        Guid? SourceNodeId, Guid? TargetNodeId,
        int PlacementStart, int PlacementEnd, int AdvancementCount, string? SeedMode,
        string? SourceNodeName, string? TargetNodeName,
        string? SourceTournamentName, string? TargetTournamentName, string? TargetTournamentStatus);

    private sealed record StandingRow(
        Guid TeamId, int TotalPoints, int? StandingRank, string? QualificationStatus,
        string? TeamName, string? TeamLogoUrl);

    private sealed record TeamLookupRow(Guid Id, string Name, string? LogoUrl);

    public sealed record ProposedMovement(
        Guid TeamId, string? TeamName, string? TeamLogoUrl,
        int CurrentStandingRank, int TotalPoints,
        Guid? SourceNodeId, string? SourceNodeName,
        Guid? TargetNodeId, string? TargetNodeName,
        Guid? TargetTournamentId,
        Guid RuleId, string? SeedMode,
        bool AlreadyRegistered);

    public sealed record AdvancementPreviewResult(
        List<ProposedMovement> Movements,
        List<string> Warnings,
        string? Message);

    public sealed record AdvancementApplyResult(
        int AdvancedCount, int QualificationCount,
        List<string> Warnings, string? Message);

    public sealed record AdvancementOverride(
        Guid TeamId, string Action); // "exclude" | "include"

    public sealed record AdvancementConnectionRow(
        Guid Id, Guid SeasonId,
        Guid? SourceNodeId, string SourceNodeName,
        Guid? TargetNodeId, string TargetNodeName,
        int PlacementStart, int PlacementEnd, int AdvancementCount);
}

