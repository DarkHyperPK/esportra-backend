using System.Text.Json;
using Dapper;
using Esportra.Api.Middleware;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Tournaments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Endpoints;

public static class PrizeDistributionEndpoints
{
    private static readonly JsonSerializerOptions SnakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static void MapPrizeDistributionEndpoints(this WebApplication app)
    {
        // ── GET /api/tournaments/{id}/prize-distribution ──────────────────────
        // Returns the current configuration plus the effective disclaimer.
        // Disclaimer surfaces automatically when organizer-managed rewards are present.
        app.MapGet("/api/tournaments/{id}/prize-distribution", async (
            Guid id,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT prize_distribution, prize_pool, currency FROM tournaments WHERE id = @id",
                new { id });

            if (row is null) return Results.NotFound();

            var config = ParseConfig((string?)row.prize_distribution);
            string effectiveDisclaimer = ResolveDisclaimer(config);

            return Results.Ok(new
            {
                prize_pool = (decimal?)row.prize_pool ?? 0m,
                currency = (string?)row.currency ?? "USD",
                distribution = config,
                disclaimer = effectiveDisclaimer,
                has_organizer_managed_rewards = HasOrganizerManagedRewards(config),
            });
        });

        // ── PUT /api/tournaments/{id}/prize-distribution ──────────────────────
        app.MapPut("/api/tournaments/{id}/prize-distribution", async (
            Guid id,
            [FromBody] SetPrizeDistributionRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            PrizeDistributionService prizeService,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, id, ct: ct))
                return Results.Forbid();

            var config = new PrizeDistributionConfig(
                "percentage",
                req.Placements.Select(p => new PrizeDistributionEntry(
                    p.Position, p.Label, p.Percentage, p.SharedCount,
                    p.Rewards?.Select(r => new PrizeReward(
                        r.Type, r.Title, r.Description,
                        r.EstimatedValue, r.Quantity, r.FulfillmentNotes)).ToList()
                )).ToList(),
                Disclaimer: req.Disclaimer);

            var validation = prizeService.Validate(config);
            if (!validation.IsValid)
                return Results.BadRequest(new { error = validation.Error });

            var json = JsonSerializer.Serialize(config, SnakeCase);

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE tournaments SET prize_distribution = @json::jsonb WHERE id = @id",
                new { id, json });

            return Results.Ok(new
            {
                distribution = config,
                disclaimer = ResolveDisclaimer(config),
                has_organizer_managed_rewards = HasOrganizerManagedRewards(config),
            });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/tournaments/{id}/prize-distribution ───────────────────
        app.MapDelete("/api/tournaments/{id}/prize-distribution", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, id, ct: ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE tournaments SET prize_distribution = '[]'::jsonb WHERE id = @id",
                new { id });

            return Results.NoContent();
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/{id}/prize-distribution/templates ────────────
        // Returns format-appropriate templates with live prize preview amounts.
        app.MapGet("/api/tournaments/{id}/prize-distribution/templates", async (
            Guid id,
            IDbConnectionFactory db,
            PrizeDistributionService prizeService) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT t.prize_pool, t.currency, t.max_teams,
                       s.format AS final_format
                FROM tournaments t
                LEFT JOIN (
                    SELECT tournament_id, format
                    FROM tournament_stages
                    WHERE tournament_id = @id
                    ORDER BY stage_order DESC
                    LIMIT 1
                ) s ON s.tournament_id = t.id
                WHERE t.id = @id
                """,
                new { id });

            if (row is null) return Results.NotFound();

            string format = (string?)row.final_format ?? "single_elimination";
            int teamCount = (int?)row.max_teams ?? 8;
            decimal prizePool = (decimal?)row.prize_pool ?? 0m;
            string currency = (string?)row.currency ?? "USD";

            var templates = prizeService.GetTemplates(format, teamCount);

            var response = templates.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                format,
                team_count = teamCount,
                distribution = t.Config,
                preview = t.Config.Placements.Select(p => new
                {
                    position = p.Position,
                    label = p.Label,
                    percentage = p.Percentage,
                    shared_count = p.SharedCount,
                    band_total = Math.Round(prizePool * p.Percentage / 100m, 2),
                    per_team = Math.Round(prizePool * p.Percentage / 100m / p.SharedCount, 2),
                    currency,
                }),
            });

            return Results.Ok(response);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/{id}/placements ──────────────────────────────
        // Public standings with prize amounts, reward lists, and disclaimer.
        app.MapGet("/api/tournaments/{id}/placements", async (
            Guid id,
            IDbConnectionFactory db,
            PlacementResolutionService resolutionService,
            HybridCache cache,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();

            var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, currency, prize_pool, prize_distribution FROM tournaments WHERE id = @id",
                new { id });

            if (tournament is null) return Results.NotFound();

            var persistedRows = (await conn.QueryAsync<dynamic>(
                """
                SELECT tp.placement, tp.placement_label, tp.prize_amount,
                       tp.prize_rewards, tp.is_tied, tp.resolved_at,
                       tp.team_id, t.name AS team_name, t.logo_url AS team_logo
                FROM tournament_placements tp
                JOIN teams t ON t.id = tp.team_id
                WHERE tp.tournament_id = @id
                ORDER BY tp.placement
                """,
                new { id })).AsList();

            string currency = (string?)tournament.currency ?? "USD";
            var config = ParseConfig((string?)tournament.prize_distribution);
            string disclaimer = ResolveDisclaimer(config);
            bool hasOrganizerRewards = HasOrganizerManagedRewards(config);

            if (persistedRows.Count > 0)
            {
                var result = persistedRows.Select(p =>
                {
                    var rewards = ParseRewards((string?)p.prize_rewards);
                    return new
                    {
                        team_id = (Guid)p.team_id,
                        team_name = (string?)p.team_name,
                        team_logo = (string?)p.team_logo,
                        placement = (int)p.placement,
                        placement_label = (string?)p.placement_label,
                        prize_amount = (decimal?)p.prize_amount ?? 0m,
                        currency,
                        rewards,
                        is_tied = (bool?)p.is_tied ?? false,
                        resolved_at = (DateTime?)p.resolved_at,
                    };
                });

                return Results.Ok(new
                {
                    placements = result,
                    disclaimer,
                    has_organizer_managed_rewards = hasOrganizerRewards,
                });
            }

            // No persisted placements — compute live with short cache to prevent DoS
            var livePlacements = await cache.GetOrCreateAsync(
                $"tournament:{id}:live-placements",
                async (_) =>
                {
                    var live = await resolutionService.ComputeCurrentAsync(id, ct);
                    return live.Select(p => new LivePlacementDto
                    {
                        TeamId = p.TeamId,
                        TeamName = p.TeamName,
                        Placement = p.Placement,
                        PlacementLabel = p.PlacementLabel,
                        PrizeAmount = p.PrizeAmount,
                        Rewards = p.Rewards,
                        IsTied = p.IsTied,
                    }).ToList();
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(10) },
                cancellationToken: ct);

            var liveResult = (livePlacements ?? []).Select(p => new
            {
                team_id = p.TeamId,
                team_name = p.TeamName,
                team_logo = (string?)null,
                placement = p.Placement,
                placement_label = p.PlacementLabel,
                prize_amount = p.PrizeAmount,
                currency,
                rewards = p.Rewards,
                is_tied = p.IsTied,
                resolved_at = (DateTime?)null,
            });

            return Results.Ok(new
            {
                placements = liveResult,
                disclaimer,
                has_organizer_managed_rewards = hasOrganizerRewards,
            });
        }).WithMetadata(new RateLimitPolicyMetadata("strict"));

        // ── POST /api/tournaments/{id}/placements/resolve ─────────────────────
        app.MapPost("/api/tournaments/{id}/placements/resolve", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            PlacementResolutionService resolutionService,
            HybridCache cache,
            CancellationToken ct,
            [FromQuery] bool force = false) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, id, ct: ct))
                return Results.Forbid();

            var placements = await resolutionService.ResolveAsync(id, force, ct);

            await cache.RemoveAsync($"tournament:{id}:live-placements", ct);

            return Results.Ok(new
            {
                resolved_count = placements.Count,
                placements = placements.Select(p => new
                {
                    team_id = p.TeamId,
                    team_name = p.TeamName,
                    placement = p.Placement,
                    placement_label = p.PlacementLabel,
                    prize_amount = p.PrizeAmount,
                    is_tied = p.IsTied,
                }),
            });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/{id}/reward-distributions ────────────────────
        // Organizer view of all non-monetary reward fulfillment statuses.
        // Lets organizers track who they have and haven't distributed rewards to.
        app.MapGet("/api/tournaments/{id}/reward-distributions", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, id, ct: ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT rd.id, rd.team_id, t.name AS team_name, rd.placement,
                       rd.reward_index, rd.reward_title, rd.reward_type,
                       rd.status, rd.notes, rd.distributed_by, rd.distributed_at,
                       tp.placement_label
                FROM tournament_reward_distributions rd
                JOIN teams t ON t.id = rd.team_id
                LEFT JOIN tournament_placements tp ON tp.tournament_id = rd.tournament_id
                    AND tp.team_id = rd.team_id
                WHERE rd.tournament_id = @id
                ORDER BY rd.placement, rd.reward_index, t.name
                """,
                new { id });

            var summary = new
            {
                tournament_id = id,
                disclaimer = PlatformDisclaimer.OrganizerManagedRewards,
                distributions = rows.Select(r => new
                {
                    id = (Guid)r.id,
                    team_id = (Guid)r.team_id,
                    team_name = (string?)r.team_name,
                    placement = (int)r.placement,
                    placement_label = (string?)r.placement_label,
                    reward_index = (int)r.reward_index,
                    reward_title = (string?)r.reward_title,
                    reward_type = (string?)r.reward_type,
                    status = (string?)r.status,
                    notes = (string?)r.notes,
                    distributed_by = (Guid?)r.distributed_by,
                    distributed_at = (DateTime?)r.distributed_at,
                }),
            };

            return Results.Ok(summary);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/tournaments/{id}/reward-distributions/{distributionId} ───
        // Organizer marks a reward as distributed, claimed, or cancelled.
        // This is a courtesy tracking tool only — the platform does not enforce
        // or guarantee fulfillment of organizer-managed rewards.
        app.MapPut("/api/tournaments/{id}/reward-distributions/{distributionId}", async (
            Guid id,
            Guid distributionId,
            [FromBody] UpdateRewardDistributionRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, id, ct: ct))
                return Results.Forbid();

            string[] allowed = ["pending", "distributed", "claimed", "cancelled"];
            if (!allowed.Contains(req.Status, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Invalid status. Allowed: {string.Join(", ", allowed)}" });

            using var conn = db.CreateConnection();
            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE tournament_reward_distributions
                SET status         = @status,
                    notes          = COALESCE(@notes, notes),
                    distributed_by = CASE WHEN @status = 'distributed' THEN @distributedBy ELSE distributed_by END,
                    distributed_at = CASE WHEN @status = 'distributed' THEN NOW() ELSE distributed_at END,
                    updated_at     = NOW()
                WHERE id = @distributionId AND tournament_id = @id
                RETURNING id, team_id, placement, reward_index, reward_title, reward_type,
                          status, notes, distributed_by, distributed_at
                """,
                new
                {
                    id,
                    distributionId,
                    status = req.Status.ToLowerInvariant(),
                    notes = req.Notes,
                    distributedBy = userCtx.UserIdGuid,
                });

            if (updated is null) return Results.NotFound();

            return Results.Ok(new
            {
                id = (Guid)updated.id,
                team_id = (Guid)updated.team_id,
                placement = (int)updated.placement,
                reward_index = (int)updated.reward_index,
                reward_title = (string?)updated.reward_title,
                reward_type = (string?)updated.reward_type,
                status = (string?)updated.status,
                notes = (string?)updated.notes,
                distributed_by = (Guid?)updated.distributed_by,
                distributed_at = (DateTime?)updated.distributed_at,
                platform_note = "This status update is a courtesy record only. " +
                                  "The platform is not responsible for the fulfillment of organizer-managed rewards.",
            });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/{id}/payouts ─────────────────────────────────
        // Organizer view of cash payout status per team.
        // payment_method distinguishes manual (organizer handles outside platform)
        // from gateway (future automated payout — same manual flow until wired).
        app.MapGet("/api/tournaments/{id}/payouts", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, id, ct: ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT payout_method, manual_payout_notes, currency FROM tournaments WHERE id = @id",
                new { id });

            if (tournament is null) return Results.NotFound();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT cp.id, cp.team_id, t.name AS team_name, cp.placement,
                       cp.amount, cp.currency, cp.payment_method, cp.manual_payment_notes,
                       cp.status, cp.initiated_by, cp.initiated_at, cp.paid_at, cp.failed_reason,
                       cp.created_at, cp.updated_at,
                       tp.placement_label
                FROM tournament_cash_payouts cp
                JOIN teams t ON t.id = cp.team_id
                LEFT JOIN tournament_placements tp ON tp.tournament_id = cp.tournament_id
                    AND tp.team_id = cp.team_id
                WHERE cp.tournament_id = @id
                ORDER BY cp.placement, t.name
                """,
                new { id });

            string payoutMethod = (string?)tournament.payout_method ?? "manual";

            return Results.Ok(new
            {
                tournament_id = id,
                payout_method = payoutMethod,
                manual_payout_notes = (string?)tournament.manual_payout_notes,
                currency = (string?)tournament.currency ?? "USD",
                gateway_available = false,
                payouts = rows.Select(r => new
                {
                    id = (Guid)r.id,
                    team_id = (Guid)r.team_id,
                    team_name = (string?)r.team_name,
                    placement = (int)r.placement,
                    placement_label = (string?)r.placement_label,
                    amount = (decimal)r.amount,
                    currency = (string?)r.currency ?? "USD",
                    payment_method = (string?)r.payment_method,
                    manual_payment_notes = (string?)r.manual_payment_notes,
                    status = (string?)r.status,
                    initiated_at = (DateTime?)r.initiated_at,
                    paid_at = (DateTime?)r.paid_at,
                    failed_reason = (string?)r.failed_reason,
                    updated_at = (DateTime?)r.updated_at,
                }),
            });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/tournaments/{id}/payouts/{payoutId} ──────────────────────
        // Organizer manually transitions payout status.
        // For 'manual' method: organizer marks paid after settling outside the platform.
        // For 'gateway' method: same manual flow until gateway integration is live.
        app.MapPut("/api/tournaments/{id}/payouts/{payoutId}", async (
            Guid id,
            Guid payoutId,
            [FromBody] UpdatePayoutRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, id, ct: ct))
                return Results.Forbid();

            string[] allowed = ["requested", "approved", "rejected", "paid", "failed"];
            if (!allowed.Contains(req.Status, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Invalid status. Allowed: {string.Join(", ", allowed)}" });

            using var conn = db.CreateConnection();

            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE tournament_cash_payouts
                SET status       = @status::payout_status,
                    paid_at      = CASE WHEN @status = 'paid' THEN NOW() ELSE paid_at END,
                    failed_reason = CASE WHEN @status = 'failed' THEN @failedReason ELSE failed_reason END,
                    updated_at   = NOW()
                WHERE id = @payoutId AND tournament_id = @id
                RETURNING id, team_id, placement, amount, currency,
                          payment_method, manual_payment_notes, status,
                          paid_at, failed_reason, updated_at
                """,
                new
                {
                    id,
                    payoutId,
                    status = req.Status.ToLowerInvariant(),
                    failedReason = req.FailedReason,
                });

            if (updated is null) return Results.NotFound();

            return Results.Ok(new
            {
                id = (Guid)updated.id,
                team_id = (Guid)updated.team_id,
                placement = (int)updated.placement,
                amount = (decimal)updated.amount,
                currency = (string?)updated.currency ?? "USD",
                payment_method = (string?)updated.payment_method,
                manual_payment_notes = (string?)updated.manual_payment_notes,
                status = (string?)updated.status,
                paid_at = (DateTime?)updated.paid_at,
                failed_reason = (string?)updated.failed_reason,
                updated_at = (DateTime?)updated.updated_at,
            });
        }).RequireAuthorization("Authenticated");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ResolveDisclaimer(PrizeDistributionConfig? config)
    {
        if (config is null) return string.Empty;

        // Use organizer's custom disclaimer if set; otherwise auto-generate when needed.
        if (!string.IsNullOrWhiteSpace(config.Disclaimer))
            return config.Disclaimer;

        return HasOrganizerManagedRewards(config)
            ? PlatformDisclaimer.OrganizerManagedRewards
            : string.Empty;
    }

    private static bool HasOrganizerManagedRewards(PrizeDistributionConfig? config) =>
        config?.Placements.Any(p =>
            p.Rewards?.Any(r => RewardType.IsOrganizerManaged(r.Type)) == true) == true;

    private static PrizeDistributionConfig? ParseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]" || json == "null") return null;
        try
        {
            return JsonSerializer.Deserialize<PrizeDistributionConfig>(json, CaseInsensitive);
        }
        catch { return null; }
    }

    private static List<object> ParseRewards(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return [];
        try
        {
            return JsonSerializer.Deserialize<List<object>>(json) ?? [];
        }
        catch { return []; }
    }
}

// ── Cached DTO (HybridCache requires concrete, serializable types) ───────────

internal sealed class LivePlacementDto
{
    public Guid TeamId { get; init; }
    public string? TeamName { get; init; }
    public int Placement { get; init; }
    public string? PlacementLabel { get; init; }
    public decimal PrizeAmount { get; init; }
    public List<PrizeReward> Rewards { get; init; } = [];
    public bool IsTied { get; init; }
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

public sealed record SetPrizeDistributionRequest(
    List<PlacementEntryRequest> Placements,
    // Organizer-supplied disclaimer. Defaults to the platform's standard text
    // when organizer-managed rewards are present.
    string? Disclaimer = null);

public sealed record PlacementEntryRequest(
    int Position,
    string Label,
    decimal Percentage,
    int SharedCount = 1,
    List<RewardEntryRequest>? Rewards = null);

public sealed record RewardEntryRequest(
    string Type,                       // RewardType constant
    string Title,                      // displayed name, e.g. "Gaming PC"
    string? Description = null,
    decimal? EstimatedValue = null,
    int Quantity = 1,
    string? FulfillmentNotes = null);  // e.g. "Contact organizer via Discord"

public sealed record UpdateRewardDistributionRequest(
    string Status,        // pending | distributed | claimed | cancelled
    string? Notes = null);

public sealed record UpdatePayoutRequest(
    string Status,             // requested | approved | rejected | paid | failed
    string? FailedReason = null);
