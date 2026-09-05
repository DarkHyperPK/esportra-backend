using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Domain: Loyalty Program — points earning, redemption, tier progression,
/// per-venue configuration, and leaderboards.
/// </summary>
public static class LoyaltyEndpoints
{
    public static void MapLoyaltyEndpoints(this WebApplication app)
    {
        // ── GET /api/loyalty/my — current user's loyalty account ─────────────
        app.MapGet("/api/loyalty/my", async (
            HttpContext ctx = null!,
            IDbConnectionFactory db = null!,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var account = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT la.id, la.user_id, la.total_points, la.lifetime_points,
                       la.current_tier, la.created_at, la.updated_at
                FROM loyalty_accounts la
                WHERE la.user_id = @userId
                """,
                new { userId = userCtx.UserIdGuid });

            if (account is null)
            {
                return Results.Ok(new
                {
                    total_points = 0,
                    lifetime_points = 0,
                    current_tier = "bronze"
                });
            }

            return Results.Ok(account);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/loyalty/my/transactions — user's loyalty history ────────
        app.MapGet("/api/loyalty/my/transactions", async (
            int limit = 20,
            int offset = 0,
            HttpContext ctx = null!,
            IDbConnectionFactory db = null!,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);

            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT lt.id, lt.account_id, lt.venue_id, lt.type, lt.points,
                       lt.description, lt.session_id, lt.created_at,
                       v.name AS venue_name
                FROM loyalty_transactions lt
                JOIN loyalty_accounts la ON la.id = lt.account_id
                JOIN venues v ON v.id = lt.venue_id
                WHERE la.user_id = @userId
                ORDER BY lt.created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { userId = userCtx.UserIdGuid, limit, offset });

            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/venues/{venueId}/loyalty/config — public config ─────────
        app.MapGet("/api/venues/{venueId}/loyalty/config", async (
            Guid venueId,
            IDbConnectionFactory db = null!,
            CancellationToken ct = default) =>
        {
            using var conn = db.CreateConnection();

            var config = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT id, venue_id, points_per_hour, bonus_multiplier,
                       min_session_minutes, tiers, rewards, is_active,
                       created_at, updated_at
                FROM venue_loyalty_config
                WHERE venue_id = @venueId AND is_active = true
                """,
                new { venueId });

            if (config is null)
            {
                return Results.Ok(new
                {
                    venue_id = venueId,
                    points_per_hour = 10,
                    bonus_multiplier = 1.0m,
                    min_session_minutes = 30,
                    tiers = new[]
                    {
                        new { name = "bronze",   min_points = 0,    multiplier = 1.0, perks = "Standard rates" },
                        new { name = "silver",   min_points = 500,  multiplier = 1.2, perks = "5% discount on sessions" },
                        new { name = "gold",     min_points = 2000, multiplier = 1.5, perks = "10% discount + priority booking" },
                        new { name = "platinum", min_points = 5000, multiplier = 2.0, perks = "15% discount + free hour monthly" }
                    },
                    rewards = new[]
                    {
                        new { id = "free_30min", name = "Free 30 Minutes", points_cost = 100, type = "time",          value = 30 },
                        new { id = "free_1hr",   name = "Free 1 Hour",     points_cost = 180, type = "time",          value = 60 },
                        new { id = "wallet_5",   name = "$5 Wallet Credit", points_cost = 200, type = "wallet_credit", value = 5 }
                    },
                    is_active = false
                });
            }

            return Results.Ok(config);
        });

        // ── PUT /api/venues/{venueId}/loyalty/config — owner upserts config ──
        app.MapPut("/api/venues/{venueId}/loyalty/config", async (
            Guid venueId,
            [FromBody] LoyaltyConfigRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Venue owner gate
            var ownerCheck = await conn.QuerySingleOrDefaultAsync<int>(
                """
                SELECT 1 FROM venue_staff
                WHERE user_id = @userId AND venue_id = @venueId
                  AND role = 'owner' AND status = 'active'
                LIMIT 1
                """,
                new { userId = userCtx.UserIdGuid, venueId });
            if (ownerCheck == 0) return Results.Unauthorized();

            // Validate
            if (req.PointsPerHour < 0)
                return Results.BadRequest(new { error = "pointsPerHour must be >= 0." });
            if (req.BonusMultiplier < 1.0m)
                return Results.BadRequest(new { error = "bonusMultiplier must be >= 1.0." });
            if (req.MinSessionMinutes < 0)
                return Results.BadRequest(new { error = "minSessionMinutes must be >= 0." });

            // Validate tiers JSON structure
            var tiersJson = req.Tiers?.GetRawText();
            var rewardsJson = req.Rewards?.GetRawText();

            if (tiersJson is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(tiersJson);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                        return Results.BadRequest(new { error = "Tiers must be a JSON array." });
                    foreach (var tier in doc.RootElement.EnumerateArray())
                    {
                        if (!tier.TryGetProperty("name", out _) || !tier.TryGetProperty("min_points", out _))
                            return Results.BadRequest(new { error = "Each tier must have 'name' and 'min_points'." });
                    }
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "Invalid tiers JSON." });
                }
            }

            if (rewardsJson is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(rewardsJson);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                        return Results.BadRequest(new { error = "Rewards must be a JSON array." });
                    foreach (var reward in doc.RootElement.EnumerateArray())
                    {
                        if (!reward.TryGetProperty("name", out _) || !reward.TryGetProperty("points_cost", out _) || !reward.TryGetProperty("type", out _))
                            return Results.BadRequest(new { error = "Each reward must have 'name', 'points_cost', and 'type'." });
                    }
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "Invalid rewards JSON." });
                }
            }

            var config = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO venue_loyalty_config
                    (venue_id, points_per_hour, bonus_multiplier, min_session_minutes,
                     tiers, rewards, is_active)
                VALUES
                    (@venueId, @pointsPerHour, @bonusMultiplier, @minSessionMinutes,
                     COALESCE(@tiers::jsonb, '[{"name":"bronze","min_points":0,"multiplier":1.0,"perks":"Standard rates"},{"name":"silver","min_points":500,"multiplier":1.2,"perks":"5% discount on sessions"},{"name":"gold","min_points":2000,"multiplier":1.5,"perks":"10% discount + priority booking"},{"name":"platinum","min_points":5000,"multiplier":2.0,"perks":"15% discount + free hour monthly"}]'::jsonb),
                     COALESCE(@rewards::jsonb, '[{"id":"free_30min","name":"Free 30 Minutes","points_cost":100,"type":"time","value":30},{"id":"free_1hr","name":"Free 1 Hour","points_cost":180,"type":"time","value":60},{"id":"wallet_5","name":"$5 Wallet Credit","points_cost":200,"type":"wallet_credit","value":5}]'::jsonb),
                     @isActive)
                ON CONFLICT (venue_id) DO UPDATE SET
                    points_per_hour     = EXCLUDED.points_per_hour,
                    bonus_multiplier    = EXCLUDED.bonus_multiplier,
                    min_session_minutes = EXCLUDED.min_session_minutes,
                    tiers               = EXCLUDED.tiers,
                    rewards             = EXCLUDED.rewards,
                    is_active           = EXCLUDED.is_active
                RETURNING id, venue_id, points_per_hour, bonus_multiplier,
                          min_session_minutes, tiers, rewards, is_active,
                          created_at, updated_at
                """,
                new
                {
                    venueId,
                    pointsPerHour = req.PointsPerHour,
                    bonusMultiplier = req.BonusMultiplier,
                    minSessionMinutes = req.MinSessionMinutes,
                    tiers = tiersJson,
                    rewards = rewardsJson,
                    isActive = req.IsActive
                });

            return Results.Ok(config);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/venues/{venueId}/loyalty/earn — award points ───────────
        app.MapPost("/api/venues/{venueId}/loyalty/earn", async (
            Guid venueId,
            [FromBody] LoyaltyEarnRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (req.Points <= 0)
                return Results.BadRequest(new { error = "Points must be greater than zero." });

            if (req.Points > 10_000)
                return Results.BadRequest(new { error = "Points per transaction cannot exceed 10,000." });

            if (!Guid.TryParse(req.UserId, out var targetUserId))
                return Results.BadRequest(new { error = "Invalid userId." });

            using var conn = db.CreateConnection();

            // Venue staff gate
            var staffCheck = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND status = 'active' LIMIT 1",
                new { userId = userCtx.UserIdGuid, venueId });
            if (staffCheck == 0) return Results.Unauthorized();

            // Verify venue loyalty program is active before awarding points
            var loyaltyActive = await conn.QuerySingleOrDefaultAsync<bool?>(
                "SELECT is_active FROM venue_loyalty_config WHERE venue_id = @venueId LIMIT 1",
                new { venueId });
            if (loyaltyActive != true)
                return Results.BadRequest(new { error = "Loyalty program is not active for this venue." });

            using var tx = conn.BeginTransaction();
            try
            {
                // 1. Find or create loyalty account
                var accountId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                    "SELECT id FROM loyalty_accounts WHERE user_id = @targetUserId",
                    new { targetUserId }, tx);

                if (accountId is null)
                {
                    accountId = await conn.QuerySingleAsync<Guid>(
                        """
                        INSERT INTO loyalty_accounts (user_id)
                        VALUES (@targetUserId)
                        ON CONFLICT (user_id) DO UPDATE SET updated_at = now()
                        RETURNING id
                        """,
                        new { targetUserId }, tx);
                }

                // 2. Credit points
                await conn.ExecuteAsync(
                    """
                    UPDATE loyalty_accounts
                    SET total_points    = total_points    + @points,
                        lifetime_points = lifetime_points + @points
                    WHERE id = @accountId
                    """,
                    new { accountId, points = req.Points }, tx);

                // 3. Record transaction
                await conn.ExecuteAsync(
                    """
                    INSERT INTO loyalty_transactions
                        (account_id, venue_id, type, points, description, session_id)
                    VALUES
                        (@accountId, @venueId, 'earn', @points, @description, @sessionId)
                    """,
                    new
                    {
                        accountId,
                        venueId,
                        points = req.Points,
                        description = req.Description,
                        sessionId = string.IsNullOrEmpty(req.SessionId)
                                        ? (Guid?)null
                                        : Guid.TryParse(req.SessionId, out var sid) ? sid : (Guid?)null
                    }, tx);

                // 4. Tier upgrade check — pull tiers from venue config
                var updatedAccount = await conn.QuerySingleAsync<dynamic>(
                    "SELECT id, user_id, total_points, lifetime_points, current_tier FROM loyalty_accounts WHERE id = @accountId",
                    new { accountId }, tx);

                var tiersJson = await conn.QuerySingleOrDefaultAsync<string>(
                    "SELECT tiers::text FROM venue_loyalty_config WHERE venue_id = @venueId AND is_active = true",
                    new { venueId }, tx);

                if (tiersJson is not null)
                {
                    var newTier = ResolveTier((int)updatedAccount.lifetime_points, tiersJson);
                    if (newTier is not null && newTier != (string)updatedAccount.current_tier)
                    {
                        await conn.ExecuteAsync(
                            "UPDATE loyalty_accounts SET current_tier = @newTier WHERE id = @accountId",
                            new { newTier, accountId }, tx);
                    }
                }

                tx.Commit();

                // 5. Return refreshed account
                var account = await conn.QuerySingleAsync<dynamic>(
                    """
                    SELECT id, user_id, total_points, lifetime_points,
                           current_tier, created_at, updated_at
                    FROM loyalty_accounts
                    WHERE id = @accountId
                    """,
                    new { accountId });

                return Results.Ok(account);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        // ── POST /api/venues/{venueId}/loyalty/redeem — redeem a reward ──────
        app.MapPost("/api/venues/{venueId}/loyalty/redeem", async (
            Guid venueId,
            [FromBody] LoyaltyRedeemRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.UserId))
                return Results.BadRequest(new { error = "userId is required." });
            if (string.IsNullOrWhiteSpace(req.RewardId))
                return Results.BadRequest(new { error = "rewardId is required." });
            if (!Guid.TryParse(req.UserId, out var targetUserId))
                return Results.BadRequest(new { error = "Invalid userId." });

            using var conn = db.CreateConnection();

            // Venue staff gate
            var staffCheck = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND status = 'active' LIMIT 1",
                new { userId = userCtx.UserIdGuid, venueId });
            if (staffCheck == 0) return Results.Unauthorized();

            // 1. Load venue loyalty config
            var configRow = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT rewards::text AS rewards_json FROM venue_loyalty_config WHERE venue_id = @venueId AND is_active = true",
                new { venueId });

            if (configRow is null)
                return Results.BadRequest(new { error = "Loyalty program is not active for this venue." });

            // 2. Find the reward in the rewards JSON
            JsonElement? matchedReward = null;
            string rewardName = "";
            int pointsCost = 0;
            string rewardType = "";
            int rewardValue = 0;

            using (var doc = JsonDocument.Parse((string)configRow.rewards_json))
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.TryGetProperty("id", out var idProp) && idProp.GetString() == req.RewardId)
                    {
                        matchedReward = el;
                        rewardName = el.GetProperty("name").GetString() ?? "";
                        pointsCost = el.GetProperty("points_cost").GetInt32();
                        rewardType = el.GetProperty("type").GetString() ?? "";
                        rewardValue = el.GetProperty("value").GetInt32();
                        break;
                    }
                }
            }

            if (matchedReward is null)
                return Results.NotFound(new { error = "Reward not found." });

            using var tx = conn.BeginTransaction();
            try
            {
                // 3. Load account with row lock
                var account = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    """
                    SELECT la.id, la.total_points
                    FROM loyalty_accounts la
                    WHERE la.user_id = @targetUserId
                    FOR UPDATE
                    """,
                    new { targetUserId }, tx);

                if (account is null)
                    return Results.BadRequest(new { error = "User has no loyalty account." });

                if ((int)account.total_points < pointsCost)
                    return Results.BadRequest(new
                    {
                        error = "Insufficient points.",
                        currentPoints = (int)account.total_points,
                        requiredPoints = pointsCost
                    });

                var accountId = (Guid)account.id;

                // 4. Deduct points
                await conn.ExecuteAsync(
                    """
                    UPDATE loyalty_accounts
                    SET total_points = total_points - @pointsCost
                    WHERE id = @accountId
                    """,
                    new { pointsCost, accountId }, tx);

                // 5. Record transaction
                await conn.ExecuteAsync(
                    """
                    INSERT INTO loyalty_transactions
                        (account_id, venue_id, type, points, description)
                    VALUES
                        (@accountId, @venueId, 'redeem', @points, @description)
                    """,
                    new
                    {
                        accountId,
                        venueId,
                        points = -pointsCost,
                        description = $"Redeemed: {rewardName}"
                    }, tx);

                // 6. If reward type is wallet_credit, top up the customer's wallet
                if (rewardType == "wallet_credit")
                {
                    // Find or create wallet
                    var walletId = await conn.QuerySingleAsync<Guid>(
                        """
                        INSERT INTO customer_wallets (user_id, venue_id, balance)
                        VALUES (@targetUserId, @venueId, 0)
                        ON CONFLICT (user_id, venue_id) DO UPDATE SET updated_at = now()
                        RETURNING id
                        """,
                        new { targetUserId, venueId }, tx);

                    var newBalance = await conn.QuerySingleAsync<decimal>(
                        """
                        UPDATE customer_wallets
                        SET balance = balance + @amount, updated_at = now()
                        WHERE id = @walletId
                        RETURNING balance
                        """,
                        new { walletId, amount = (decimal)rewardValue }, tx);

                    await conn.ExecuteAsync(
                        """
                        INSERT INTO wallet_transactions
                            (wallet_id, venue_id, type, amount, balance_after, description, created_by)
                        VALUES
                            (@walletId, @venueId, 'bonus', @amount, @newBalance, @description, @createdBy)
                        """,
                        new
                        {
                            walletId,
                            venueId,
                            amount = (decimal)rewardValue,
                            newBalance,
                            description = $"Loyalty reward: {rewardName}",
                            createdBy = userCtx.UserIdGuid
                        }, tx);
                }

                tx.Commit();

                // 7. Return updated account + reward details
                var refreshed = await conn.QuerySingleAsync<dynamic>(
                    """
                    SELECT id, user_id, total_points, lifetime_points,
                           current_tier, created_at, updated_at
                    FROM loyalty_accounts
                    WHERE id = @accountId
                    """,
                    new { accountId });

                return Results.Ok(new
                {
                    account = refreshed,
                    reward = new
                    {
                        id = req.RewardId,
                        name = rewardName,
                        points_cost = pointsCost,
                        type = rewardType,
                        value = rewardValue
                    }
                });
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        // ── GET /api/venues/{venueId}/loyalty/leaderboard — top 10 ───────────
        app.MapGet("/api/venues/{venueId}/loyalty/leaderboard", async (
            Guid venueId,
            IDbConnectionFactory db = null!,
            CancellationToken ct = default) =>
        {
            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT DENSE_RANK() OVER (ORDER BY la.lifetime_points DESC) AS rank,
                       u.raw_user_meta_data->>'display_name' AS display_name,
                       la.current_tier,
                       la.lifetime_points
                FROM loyalty_accounts la
                JOIN auth.users u ON u.id = la.user_id
                WHERE EXISTS (
                    SELECT 1 FROM loyalty_transactions lt
                    WHERE lt.account_id = la.id AND lt.venue_id = @venueId
                )
                ORDER BY la.lifetime_points DESC
                LIMIT 10
                """,
                new { venueId });

            return Results.Ok(rows);
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve the highest tier a user qualifies for based on lifetime points.
    /// Tiers JSON: [{"name":"bronze","min_points":0,...}, ...]
    /// </summary>
    private static string? ResolveTier(int lifetimePoints, string tiersJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(tiersJson);
            string? best = null;
            int bestMin = -1;

            foreach (var tier in doc.RootElement.EnumerateArray())
            {
                var minPoints = tier.GetProperty("min_points").GetInt32();
                var name = tier.GetProperty("name").GetString();
                if (lifetimePoints >= minPoints && minPoints > bestMin)
                {
                    best = name;
                    bestMin = minPoints;
                }
            }

            return best;
        }
        catch (Exception ex)
        {
            // Don't crash the caller — static method can't use ILogger, use trace diagnostics
            System.Diagnostics.Trace.TraceWarning($"[Loyalty] ResolveTier failed: {ex.Message}");
            return "bronze"; // safe fallback
        }
    }

    // ── Request DTOs ────────────────────────────────────────────────────────────

    private sealed record LoyaltyConfigRequest(
        int PointsPerHour,
        decimal BonusMultiplier,
        int MinSessionMinutes,
        JsonElement? Tiers,
        JsonElement? Rewards,
        bool IsActive);

    private sealed record LoyaltyEarnRequest(
        string UserId,
        int Points,
        string? Description,
        string? SessionId);

    private sealed record LoyaltyRedeemRequest(
        string UserId,
        string RewardId);
}
