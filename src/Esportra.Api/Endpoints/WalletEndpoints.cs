using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Domain: Customer Wallets — prepaid balance per user per venue,
/// with top-up / deduct operations and a full transaction audit trail.
/// </summary>
public static class WalletEndpoints
{
    public static void MapWalletEndpoints(this WebApplication app)
    {
        // ── GET /api/venues/{venueId}/wallets — list active wallets ──────────
        app.MapGet("/api/venues/{venueId}/wallets", async (
            Guid                 venueId,
            string?              search,
            int                  limit  = 20,
            int                  offset = 0,
            HttpContext          ctx    = null!,
            IDbConnectionFactory db     = null!,
            CancellationToken    ct     = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            limit  = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);

            using var conn = db.CreateConnection();

            // Venue staff gate
            var staffCheck = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND accepted_at IS NOT NULL LIMIT 1",
                new { userId = userCtx.UserIdGuid, venueId });
            if (staffCheck == 0) return Results.Unauthorized();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT w.id, w.user_id, w.venue_id, w.balance, w.currency,
                       w.is_active, w.created_at, w.updated_at,
                       u.raw_user_meta_data->>'display_name' AS display_name,
                       u.email
                FROM customer_wallets w
                JOIN auth.users u ON u.id = w.user_id
                WHERE w.venue_id = @venueId
                  AND w.is_active = true
                  AND (
                    @search IS NULL
                    OR u.email ILIKE '%' || @search || '%'
                    OR u.raw_user_meta_data->>'display_name' ILIKE '%' || @search || '%'
                  )
                ORDER BY w.updated_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { venueId, search, limit, offset });

            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/venues/{venueId}/wallets/{walletId} — single wallet ────
        app.MapGet("/api/venues/{venueId}/wallets/{walletId}", async (
            Guid                 venueId,
            Guid                 walletId,
            HttpContext          ctx    = null!,
            IDbConnectionFactory db     = null!,
            CancellationToken    ct     = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Allow access if venue staff OR wallet owner
            var wallet = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT w.id, w.user_id, w.venue_id, w.balance, w.currency,
                       w.is_active, w.created_at, w.updated_at,
                       u.raw_user_meta_data->>'display_name' AS display_name,
                       u.email
                FROM customer_wallets w
                JOIN auth.users u ON u.id = w.user_id
                WHERE w.id = @walletId AND w.venue_id = @venueId
                """,
                new { walletId, venueId });

            if (wallet is null) return Results.NotFound();

            // Authorize: owner or staff
            bool isOwner = (Guid)wallet.user_id == userCtx.UserIdGuid;
            if (!isOwner)
            {
                var staffCheck = await conn.QuerySingleOrDefaultAsync<int>(
                    "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND accepted_at IS NOT NULL LIMIT 1",
                    new { userId = userCtx.UserIdGuid, venueId });
                if (staffCheck == 0) return Results.Unauthorized();
            }

            return Results.Ok(wallet);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/venues/{venueId}/wallets/topup — add funds ────────────
        app.MapPost("/api/venues/{venueId}/wallets/topup", async (
            Guid                                venueId,
            [FromBody] WalletTopupRequest        req,
            HttpContext                          ctx,
            IDbConnectionFactory                db,
            CancellationToken                   ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (req.Amount <= 0)
                return Results.BadRequest(new { error = "Amount must be greater than zero." });

            if (!Guid.TryParse(req.UserId, out var targetUserId))
                return Results.BadRequest(new { error = "Invalid userId." });

            using var conn = db.CreateConnection();
            conn.Open();

            // Venue staff gate
            var staffCheck = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND accepted_at IS NOT NULL LIMIT 1",
                new { userId = userCtx.UserIdGuid, venueId });
            if (staffCheck == 0) return Results.Unauthorized();

            // Verify target user exists
            var userExists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM auth.users WHERE id = @targetUserId)",
                new { targetUserId });
            if (!userExists)
                return Results.BadRequest(new { error = "User not found." });

            using var tx = conn.BeginTransaction();
            try
            {
                // 1. Find or create wallet
                var walletId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                    "SELECT id FROM customer_wallets WHERE user_id = @targetUserId AND venue_id = @venueId",
                    new { targetUserId, venueId }, tx);

                if (walletId is null)
                {
                    walletId = await conn.QuerySingleAsync<Guid>(
                        """
                        INSERT INTO customer_wallets (user_id, venue_id, balance, currency)
                        VALUES (@targetUserId, @venueId, 0,
                                COALESCE((SELECT currency FROM venue_billing_config WHERE venue_id = @venueId LIMIT 1), 'SAR'))
                        ON CONFLICT (user_id, venue_id) DO UPDATE SET updated_at = now()
                        RETURNING id
                        """,
                        new { targetUserId, venueId }, tx);
                }

                // 2. Credit balance
                var newBalance = await conn.QuerySingleAsync<decimal>(
                    """
                    UPDATE customer_wallets
                    SET balance = balance + @amount, updated_at = now()
                    WHERE id = @walletId
                    RETURNING balance
                    """,
                    new { walletId, amount = req.Amount }, tx);

                // 3. Record transaction
                await conn.ExecuteAsync(
                    """
                    INSERT INTO wallet_transactions
                        (wallet_id, venue_id, type, amount, balance_after, description, created_by)
                    VALUES
                        (@walletId, @venueId, 'topup', @amount, @newBalance, @description, @createdBy)
                    """,
                    new
                    {
                        walletId,
                        venueId,
                        amount      = req.Amount,
                        newBalance,
                        description = req.Description,
                        createdBy   = userCtx.UserIdGuid
                    }, tx);

                tx.Commit();

                // 4. Return updated wallet
                var wallet = await conn.QuerySingleAsync<dynamic>(
                    """
                    SELECT w.id, w.user_id, w.venue_id, w.balance, w.currency,
                           w.is_active, w.created_at, w.updated_at,
                           u.raw_user_meta_data->>'display_name' AS display_name,
                           u.email
                    FROM customer_wallets w
                    JOIN auth.users u ON u.id = w.user_id
                    WHERE w.id = @walletId
                    """,
                    new { walletId });

                return Results.Ok(wallet);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        // ── POST /api/venues/{venueId}/wallets/deduct — subtract funds ──────
        app.MapPost("/api/venues/{venueId}/wallets/deduct", async (
            Guid                                venueId,
            [FromBody] WalletDeductRequest       req,
            HttpContext                          ctx,
            IDbConnectionFactory                db,
            CancellationToken                   ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (req.Amount <= 0)
                return Results.BadRequest(new { error = "Amount must be greater than zero." });

            if (!Guid.TryParse(req.WalletId, out var walletId))
                return Results.BadRequest(new { error = "Invalid walletId." });

            using var conn = db.CreateConnection();
            conn.Open();

            // Venue staff gate
            var staffCheck = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND accepted_at IS NOT NULL LIMIT 1",
                new { userId = userCtx.UserIdGuid, venueId });
            if (staffCheck == 0) return Results.Unauthorized();

            using var tx = conn.BeginTransaction();
            try
            {
                // 1. Fetch current balance (lock row via FOR UPDATE)
                var current = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT id, balance
                    FROM customer_wallets
                    WHERE id = @walletId AND venue_id = @venueId AND is_active = true
                    FOR UPDATE
                    """,
                    new { walletId, venueId }, tx);

                if (current is null)
                    return Results.NotFound(new { error = "Wallet not found." });

                if ((decimal)current.balance < req.Amount)
                    return Results.BadRequest(new { error = "Insufficient balance.", currentBalance = (decimal)current.balance });

                // 2. Debit balance
                var newBalance = await conn.QuerySingleAsync<decimal>(
                    """
                    UPDATE customer_wallets
                    SET balance = balance - @amount, updated_at = now()
                    WHERE id = @walletId
                    RETURNING balance
                    """,
                    new { walletId, amount = req.Amount }, tx);

                // 3. Record transaction
                await conn.ExecuteAsync(
                    """
                    INSERT INTO wallet_transactions
                        (wallet_id, venue_id, type, amount, balance_after, description, reference_id, created_by)
                    VALUES
                        (@walletId, @venueId, 'deduct', @amount, @newBalance, @description, @referenceId, @createdBy)
                    """,
                    new
                    {
                        walletId,
                        venueId,
                        amount      = req.Amount,
                        newBalance,
                        description = req.Description,
                        referenceId = req.ReferenceId,
                        createdBy   = userCtx.UserIdGuid
                    }, tx);

                tx.Commit();

                // 4. Return updated wallet
                var wallet = await conn.QuerySingleAsync<dynamic>(
                    """
                    SELECT w.id, w.user_id, w.venue_id, w.balance, w.currency,
                           w.is_active, w.created_at, w.updated_at,
                           u.raw_user_meta_data->>'display_name' AS display_name,
                           u.email
                    FROM customer_wallets w
                    JOIN auth.users u ON u.id = w.user_id
                    WHERE w.id = @walletId
                    """,
                    new { walletId });

                return Results.Ok(wallet);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        // ── GET /api/wallets/my — gamer's own wallets across venues ─────────
        app.MapGet("/api/wallets/my", async (
            HttpContext          ctx      = null!,
            IDbConnectionFactory db       = null!,
            int                  page     = 1,
            int                  pageSize = 20,
            CancellationToken    ct       = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            page     = Math.Max(page, 1);
            pageSize = Math.Clamp(pageSize, 1, 50);
            var offset = (page - 1) * pageSize;

            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT w.id, w.user_id, w.venue_id, w.balance, w.currency,
                       w.is_active, w.created_at, w.updated_at,
                       v.name AS venue_name
                FROM customer_wallets w
                JOIN venues v ON v.id = w.venue_id
                WHERE w.user_id = @userId AND w.is_active = true
                ORDER BY w.updated_at DESC
                LIMIT @pageSize OFFSET @offset
                """,
                new { userId = userCtx.UserIdGuid, pageSize, offset });

            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/venues/{venueId}/wallets/{walletId}/transactions ────────
        app.MapGet("/api/venues/{venueId}/wallets/{walletId}/transactions", async (
            Guid                 venueId,
            Guid                 walletId,
            int                  limit  = 20,
            int                  offset = 0,
            HttpContext          ctx    = null!,
            IDbConnectionFactory db     = null!,
            CancellationToken    ct     = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            limit  = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);

            using var conn = db.CreateConnection();

            // Verify wallet exists and belongs to this venue
            var wallet = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT id, user_id FROM customer_wallets WHERE id = @walletId AND venue_id = @venueId",
                new { walletId, venueId });

            if (wallet is null) return Results.NotFound();

            // Authorize: owner or staff
            bool isOwner = (Guid)wallet.user_id == userCtx.UserIdGuid;
            if (!isOwner)
            {
                var staffCheck = await conn.QuerySingleOrDefaultAsync<int>(
                    "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND accepted_at IS NOT NULL LIMIT 1",
                    new { userId = userCtx.UserIdGuid, venueId });
                if (staffCheck == 0) return Results.Unauthorized();
            }

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT t.id, t.wallet_id, t.venue_id, t.type, t.amount,
                       t.balance_after, t.description, t.reference_id,
                       t.created_by, t.created_at
                FROM wallet_transactions t
                WHERE t.wallet_id = @walletId
                ORDER BY t.created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { walletId, limit, offset });

            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/venues/{venueId}/wallets/find-or-create ───────────────
        app.MapPost("/api/venues/{venueId}/wallets/find-or-create", async (
            Guid                                     venueId,
            [FromBody] WalletFindOrCreateRequest      req,
            HttpContext                               ctx,
            IDbConnectionFactory                     db,
            CancellationToken                        ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!Guid.TryParse(req.UserId, out var targetUserId))
                return Results.BadRequest(new { error = "Invalid userId." });

            using var conn = db.CreateConnection();

            // Venue staff gate
            var staffCheck = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND accepted_at IS NOT NULL LIMIT 1",
                new { userId = userCtx.UserIdGuid, venueId });
            if (staffCheck == 0) return Results.Unauthorized();

            // Verify target user exists
            var userExists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM auth.users WHERE id = @targetUserId)",
                new { targetUserId });
            if (!userExists)
                return Results.BadRequest(new { error = "User not found." });

            var wallet = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO customer_wallets (user_id, venue_id, balance, currency)
                VALUES (@targetUserId, @venueId, 0,
                        COALESCE((SELECT currency FROM venue_billing_config WHERE venue_id = @venueId LIMIT 1), 'SAR'))
                ON CONFLICT (user_id, venue_id) DO UPDATE SET updated_at = now()
                RETURNING id, user_id, venue_id, balance, currency, is_active, created_at, updated_at
                """,
                new { targetUserId, venueId });

            return Results.Ok(wallet);
        }).RequireAuthorization("Authenticated");
    }

    // ── Request DTOs ────────────────────────────────────────────────────────────

    private sealed record WalletTopupRequest(
        string  UserId,
        decimal Amount,
        string? Description);

    private sealed record WalletDeductRequest(
        string  WalletId,
        decimal Amount,
        string? Description,
        string? ReferenceId);

    private sealed record WalletFindOrCreateRequest(
        string UserId);
}
