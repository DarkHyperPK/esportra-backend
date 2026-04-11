using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class SessionRefundEndpoints
{
    public static void MapSessionRefundEndpoints(this WebApplication app)
    {
        // ── POST /api/venues/{id}/sessions/{sessionId}/refund ──────────────
        // Owner or manager issues a full or partial session refund.
        app.MapPost("/api/venues/{id}/sessions/{sessionId}/refund", async (
            Guid                       id,
            Guid                       sessionId,
            [FromBody] RefundRequest   req,
            HttpContext                 ctx,
            IDbConnectionFactory       db,
            ILogger<Program>           logger,
            CancellationToken          ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // 1. Authorization: owner or manager only
            var staffRole = await conn.ExecuteScalarAsync<string?>(
                "SELECT role FROM venue_staff WHERE venue_id = @id AND user_id = @userId AND accepted_at IS NOT NULL LIMIT 1",
                new { id, userId = userCtx.UserIdGuid });

            if (staffRole is not ("owner" or "manager"))
                return Results.Json(new { error = "Only owners and managers can issue refunds." }, statusCode: 403);

            // 2. Validate input
            if (req.Amount <= 0)
                return Results.BadRequest(new { error = "Refund amount must be greater than zero." });
            if (req.Amount != Math.Round(req.Amount, 2))
                return Results.BadRequest(new { error = "Amount can have at most 2 decimal places." });
            if (string.IsNullOrWhiteSpace(req.Reason))
                return Results.BadRequest(new { error = "A reason is required for every refund." });
            if (req.Method is not ("wallet" or "cash"))
                return Results.BadRequest(new { error = "Method must be 'wallet' or 'cash'." });

            // 3. Verify session exists and belongs to this venue
            var session = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT id, venue_id, total_charged, user_id, ended_at,
                       COALESCE(refund_amount, 0) AS already_refunded
                FROM venue_sessions
                WHERE id = @sessionId AND venue_id = @id
                """,
                new { sessionId, id });

            if (session is null)
                return Results.NotFound(new { error = "Session not found." });

            // 4. Business rules
            decimal totalCharged = (decimal)(session.total_charged ?? 0m);
            decimal alreadyRefunded = (decimal)session.already_refunded;
            decimal maxRefundable = totalCharged - alreadyRefunded;

            if (totalCharged <= 0)
                return Results.BadRequest(new { error = "This session has no charge to refund." });

            if (req.Amount > maxRefundable)
                return Results.BadRequest(new { error = $"Amount exceeds refundable balance. Maximum: {maxRefundable:F2}" });

            if (session.ended_at is null)
                return Results.BadRequest(new { error = "Cannot refund an active session. End it first." });

            if (req.Method == "wallet" && session.user_id is null)
                return Results.BadRequest(new { error = "Cannot refund to wallet: session has no linked customer. Use cash refund." });

            // 5. Execute atomic refund via database function
            try
            {
                var result = await conn.QueryFirstAsync<dynamic>(
                    "SELECT * FROM process_session_refund(@sessionId, @venueId, @amount, @method, @reason, @refundedBy)",
                    new
                    {
                        sessionId,
                        venueId = id,
                        amount = req.Amount,
                        method = req.Method,
                        reason = req.Reason.Trim(),
                        refundedBy = userCtx.UserIdGuid,
                    });

                logger.LogInformation(
                    "[Refund] Session {SessionId} refunded {Amount} via {Method} by {UserId} — reason: {Reason}",
                    sessionId, req.Amount, req.Method, userCtx.UserIdGuid, req.Reason);

                return Results.Ok(new
                {
                    refundId = (Guid)result.refund_id,
                    walletTxnId = result.wallet_txn_id as Guid?,
                    totalRefunded = (decimal)result.total_refunded,
                    maxRefundable = totalCharged,
                    remaining = totalCharged - (decimal)result.total_refunded,
                    method = req.Method,
                });
            }
            catch (Npgsql.PostgresException ex) when (ex.MessageText.Contains("exceeds"))
            {
                return Results.Conflict(new { error = "Refund race condition: another refund was processed simultaneously. Please retry." });
            }
            catch (Npgsql.PostgresException ex) when (ex.MessageText.Contains("no linked user"))
            {
                return Results.BadRequest(new { error = "Cannot refund to wallet: no customer linked to this session." });
            }
        }).RequireAuthorization("Authenticated");

        // ── GET /api/venues/{id}/sessions/{sessionId}/refunds ──────────────
        // Get all refunds for a specific session.
        app.MapGet("/api/venues/{id}/sessions/{sessionId}/refunds", async (
            Guid                 id,
            Guid                 sessionId,
            HttpContext           ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var isMember = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM venue_staff WHERE venue_id = @id AND user_id = @userId AND accepted_at IS NOT NULL)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isMember) return Results.Forbid();

            var refunds = await conn.QueryAsync<dynamic>(
                """
                SELECT sr.id, sr.amount, sr.method, sr.reason, sr.created_at,
                       sr.wallet_txn_id,
                       u.raw_user_meta_data->>'display_name' AS refunded_by_name,
                       u.email AS refunded_by_email
                FROM session_refunds sr
                JOIN auth.users u ON u.id = sr.refunded_by
                WHERE sr.session_id = @sessionId AND sr.venue_id = @id
                ORDER BY sr.created_at DESC
                """,
                new { sessionId, id });

            return Results.Ok(refunds);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/venues/{id}/refunds ───────────────────────────────────
        // Refund history for the entire venue with pagination and filters.
        app.MapGet("/api/venues/{id}/refunds", async (
            Guid                 id,
            HttpContext           ctx,
            IDbConnectionFactory db,
            [FromQuery] int      page = 1,
            [FromQuery] int      pageSize = 25,
            [FromQuery] string?  method = null,
            [FromQuery] string?  from = null,
            [FromQuery] string?  to = null,
            CancellationToken    ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Only owner/manager can view venue-wide refund history
            var staffRole = await conn.ExecuteScalarAsync<string?>(
                "SELECT role FROM venue_staff WHERE venue_id = @id AND user_id = @userId AND accepted_at IS NOT NULL LIMIT 1",
                new { id, userId = userCtx.UserIdGuid });
            if (staffRole is not ("owner" or "manager"))
                return Results.Forbid();

            var offset = (Math.Max(1, page) - 1) * Math.Clamp(pageSize, 1, 100);
            var safePageSize = Math.Clamp(pageSize, 1, 100);

            // Build dynamic where
            var conditions = new List<string> { "sr.venue_id = @id" };
            if (method is "wallet" or "cash") conditions.Add("sr.method = @method");
            if (from is not null) conditions.Add("sr.created_at >= @fromDate::timestamptz");
            if (to is not null) conditions.Add("sr.created_at <= @toDate::timestamptz");
            var where = string.Join(" AND ", conditions);

            var totalCount = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM session_refunds sr WHERE {where}",
                new { id, method, fromDate = from, toDate = to });

            var refunds = await conn.QueryAsync<dynamic>(
                $"""
                SELECT sr.id, sr.session_id, sr.amount, sr.method, sr.reason, sr.created_at,
                       vs.station_id, vs.total_charged AS session_total,
                       COALESCE(vs.refund_amount, 0) AS session_total_refunded,
                       cu.raw_user_meta_data->>'display_name' AS customer_name,
                       cu.email AS customer_email,
                       ru.raw_user_meta_data->>'display_name' AS refunded_by_name
                FROM session_refunds sr
                JOIN venue_sessions vs ON vs.id = sr.session_id
                LEFT JOIN auth.users cu ON cu.id = vs.user_id
                JOIN auth.users ru ON ru.id = sr.refunded_by
                WHERE {where}
                ORDER BY sr.created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { id, method, fromDate = from, toDate = to, limit = safePageSize, offset });

            // Totals summary
            var summary = await conn.QueryFirstAsync<dynamic>(
                $"""
                SELECT COALESCE(SUM(sr.amount), 0) AS total_refunded,
                       COUNT(*) AS refund_count,
                       COALESCE(SUM(CASE WHEN sr.method = 'wallet' THEN sr.amount END), 0) AS wallet_total,
                       COALESCE(SUM(CASE WHEN sr.method = 'cash' THEN sr.amount END), 0) AS cash_total
                FROM session_refunds sr
                WHERE {where}
                """,
                new { id, method, fromDate = from, toDate = to });

            return Results.Ok(new
            {
                refunds,
                totalCount,
                page = Math.Max(1, page),
                pageSize = safePageSize,
                summary = new
                {
                    totalRefunded = (decimal)summary.total_refunded,
                    refundCount = (long)summary.refund_count,
                    walletTotal = (decimal)summary.wallet_total,
                    cashTotal = (decimal)summary.cash_total,
                },
            });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/venues/{id}/sessions — list sessions (needed for refund UI) ──
        // Returns recent ended sessions with refund status.
        app.MapGet("/api/venues/{id}/sessions", async (
            Guid                 id,
            HttpContext           ctx,
            IDbConnectionFactory db,
            [FromQuery] int      page = 1,
            [FromQuery] int      pageSize = 25,
            [FromQuery] string?  search = null,
            [FromQuery] bool     refundedOnly = false,
            CancellationToken    ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var isMember = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM venue_staff WHERE venue_id = @id AND user_id = @userId AND accepted_at IS NOT NULL)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isMember) return Results.Forbid();

            var offset = (Math.Max(1, page) - 1) * Math.Clamp(pageSize, 1, 100);
            var safePageSize = Math.Clamp(pageSize, 1, 100);

            var conditions = new List<string> { "vs.venue_id = @id", "vs.ended_at IS NOT NULL" };
            if (refundedOnly) conditions.Add("vs.refunded_at IS NOT NULL");
            if (!string.IsNullOrWhiteSpace(search))
                conditions.Add("(vs.station_id ILIKE @q OR u.email ILIKE @q OR u.raw_user_meta_data->>'display_name' ILIKE @q)");
            var where = string.Join(" AND ", conditions);

            var totalCount = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM venue_sessions vs LEFT JOIN auth.users u ON u.id = vs.user_id WHERE {where}",
                new { id, q = $"%{search}%" });

            var sessions = await conn.QueryAsync<dynamic>(
                $"""
                SELECT vs.id, vs.station_id, vs.session_type, vs.total_charged,
                       vs.started_at, vs.ended_at, vs.zone,
                       COALESCE(vs.refund_amount, 0) AS refund_amount,
                       vs.refunded_at, vs.refund_method,
                       u.raw_user_meta_data->>'display_name' AS customer_name,
                       u.email AS customer_email
                FROM venue_sessions vs
                LEFT JOIN auth.users u ON u.id = vs.user_id
                WHERE {where}
                ORDER BY vs.ended_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { id, q = $"%{search}%", limit = safePageSize, offset });

            return Results.Ok(new { sessions, totalCount, page = Math.Max(1, page), pageSize = safePageSize });
        }).RequireAuthorization("Authenticated");
    }

    // ── Request DTOs ──────────────────────────────────────────────────────
    private sealed record RefundRequest(decimal Amount, string Method, string Reason);
}
