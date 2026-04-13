using Dapper;
using Esportra.Api.Hubs;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using System.Text.Json;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Domain: Point of Sale — F&amp;B order management for venues.
/// Handles order creation, status lifecycle, stock tracking,
/// and kitchen display queries with real-time SignalR broadcasts.
/// </summary>
public static class POSEndpoints
{
    private static readonly string[] ValidPaymentMethods = ["cash", "card", "balance", "split"];
    private static readonly string[] ValidStatuses = ["pending", "preparing", "ready", "delivered", "cancelled"];

    public static void MapPOSEndpoints(this WebApplication app)
    {
        // ═══════════════════════════════════════════════════════════════════════
        //  POST /api/venues/{venueId}/pos/orders — create order
        // ═══════════════════════════════════════════════════════════════════════
        app.MapPost("/api/venues/{venueId}/pos/orders", async (
            Guid venueId,
            [FromBody] CreatePOSOrderRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<LiveHub> hubContext,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            // ── Validate request ────────────────────────────────────────
            if (req.Items is null || req.Items.Length == 0)
                return Results.BadRequest(new { error = "At least one item is required." });

            if (req.Items.Length > 50)
                return Results.BadRequest(new { error = "Maximum 50 items per order." });

            foreach (var item in req.Items)
            {
                if (item.Quantity <= 0)
                    return Results.BadRequest(new { error = $"Quantity must be positive for item {item.ItemId}." });
                if (item.Quantity > 100)
                    return Results.BadRequest(new { error = $"Quantity exceeds maximum (100) for item {item.ItemId}." });
            }

            if (req.PaymentMethod is not null && !ValidPaymentMethods.Contains(req.PaymentMethod))
                return Results.BadRequest(new { error = $"Invalid payment method. Must be one of: {string.Join(", ", ValidPaymentMethods)}" });

            using var conn = db.CreateConnection();
            conn.Open();

            // ── Fetch & validate menu items ─────────────────────────────
            var itemIds = req.Items.Select(i => i.ItemId).Distinct().ToArray();
            var menuItems = (await ((System.Data.IDbConnection)conn).QueryAsync<MenuItemRow>(
                """
                SELECT id, name, category, price, is_available, stock_count
                FROM venue_menu_items
                WHERE venue_id = @VenueId AND id = ANY(@ItemIds)
                """,
                new { VenueId = venueId, ItemIds = itemIds })).ToList();

            if (menuItems.Count != itemIds.Length)
            {
                var found = menuItems.Select(m => m.Id).ToHashSet();
                var missing = itemIds.Where(id => !found.Contains(id)).ToArray();
                return Results.BadRequest(new { error = "Some items not found.", missing_item_ids = missing });
            }

            var unavailable = menuItems.Where(m => !m.IsAvailable).Select(m => m.Id).ToArray();
            if (unavailable.Length > 0)
                return Results.BadRequest(new { error = "Some items are unavailable.", unavailable_item_ids = unavailable });

            // ── Build line items & check stock ──────────────────────────
            var menuLookup = menuItems.ToDictionary(m => m.Id);
            var lineItems = new List<object>();
            var subtotal = 0m;

            foreach (var reqItem in req.Items)
            {
                var menu = menuLookup[reqItem.ItemId];

                // Stock check (null = unlimited)
                if (menu.StockCount is not null && menu.StockCount < reqItem.Quantity)
                    return Results.BadRequest(new
                    {
                        error = $"Insufficient stock for '{menu.Name}'.",
                        item_id = menu.Id,
                        available = menu.StockCount,
                        requested = reqItem.Quantity
                    });

                var lineTotal = menu.Price * reqItem.Quantity;
                subtotal += lineTotal;

                lineItems.Add(new
                {
                    item_id = reqItem.ItemId,
                    name = menu.Name,
                    category = menu.Category,
                    quantity = reqItem.Quantity,
                    unit_price = menu.Price,
                    total = lineTotal
                });
            }

            var total = subtotal; // tax/discount can be added later via business rules

            // ── Decrement stock (transactional) ─────────────────────────
            using var tx = conn.BeginTransaction();
            try
            {
                foreach (var reqItem in req.Items)
                {
                    var menu = menuLookup[reqItem.ItemId];
                    if (menu.StockCount is not null)
                    {
                        var affected = await conn.ExecuteAsync(
                            """
                            UPDATE venue_menu_items
                            SET stock_count = stock_count - @Qty
                            WHERE id = @ItemId AND venue_id = @VenueId
                              AND stock_count >= @Qty
                            """,
                            new { Qty = reqItem.Quantity, ItemId = reqItem.ItemId, VenueId = venueId },
                            tx);

                        if (affected == 0)
                        {
                            tx.Rollback();
                            return Results.Conflict(new
                            {
                                error = $"Stock changed for '{menu.Name}'. Please retry.",
                                item_id = menu.Id
                            });
                        }
                    }
                }

                // ── Insert order ────────────────────────────────────────
                var itemsJson = JsonSerializer.Serialize(lineItems);
                var order = await conn.QuerySingleAsync<dynamic>(
                    """
                    INSERT INTO pos_orders
                        (venue_id, session_id, member_id, station_id, items,
                         subtotal, tax, discount, total, payment_method,
                         status, notes, created_by)
                    VALUES
                        (@VenueId, @SessionId, @MemberId, @StationId, @Items::jsonb,
                         @Subtotal, 0, 0, @Total, @PaymentMethod,
                         'pending', @Notes, @CreatedBy)
                    RETURNING id, venue_id, session_id, member_id, station_id, items,
                              subtotal, tax, discount, total, payment_method,
                              status, notes, created_by, created_at, updated_at
                    """,
                    new
                    {
                        VenueId = venueId,
                        SessionId = req.SessionId,
                        MemberId = req.MemberId,
                        StationId = req.StationId,
                        Items = itemsJson,
                        Subtotal = subtotal,
                        Total = total,
                        PaymentMethod = req.PaymentMethod ?? "cash",
                        Notes = req.Notes,
                        CreatedBy = user.UserIdGuid,
                    },
                    tx);

                tx.Commit();

                // ── Broadcast to venue group ────────────────────────────
                _ = hubContext.Clients
                    .Group(LiveHub.VenueGroup(venueId.ToString()))
                    .SendAsync(LiveHubEvents.POSOrderCreated, new
                    {
                        order_id = (Guid)order.id,
                        venue_id = venueId,
                        station_id = req.StationId,
                        status = "pending",
                        total,
                        item_count = req.Items.Length,
                        created_at = (DateTimeOffset)order.created_at,
                    }, ct);

                return Results.Created($"/api/venues/{venueId}/pos/orders/{order.id}", order);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        })
        .RequireAuthorization("Authenticated")
        .WithTags("POS");

        // ═══════════════════════════════════════════════════════════════════════
        //  GET /api/venues/{venueId}/pos/orders — list orders
        // ═══════════════════════════════════════════════════════════════════════
        app.MapGet("/api/venues/{venueId}/pos/orders", async (
            Guid venueId,
            [FromQuery] string? status,
            [FromQuery] Guid? session_id,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            HttpContext ctx = null!,
            IDbConnectionFactory db = null!,
            CancellationToken ct = default) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            if (status is not null && !ValidStatuses.Contains(status))
                return Results.BadRequest(new { error = $"Invalid status filter. Must be one of: {string.Join(", ", ValidStatuses)}" });

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var offset = (page - 1) * pageSize;

            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, venue_id, session_id, member_id, station_id, items,
                       subtotal, tax, discount, total, payment_method,
                       status, notes, created_by, created_at, updated_at
                FROM pos_orders
                WHERE venue_id = @VenueId
                  AND (@Status IS NULL OR status = @Status)
                  AND (@SessionId IS NULL OR session_id = @SessionId)
                ORDER BY created_at DESC
                LIMIT @Limit OFFSET @Offset
                """,
                new
                {
                    VenueId = venueId,
                    Status = status,
                    SessionId = session_id,
                    Limit = pageSize,
                    Offset = offset
                });

            var total = await conn.QuerySingleAsync<int>(
                """
                SELECT COUNT(*)
                FROM pos_orders
                WHERE venue_id = @VenueId
                  AND (@Status IS NULL OR status = @Status)
                  AND (@SessionId IS NULL OR session_id = @SessionId)
                """,
                new { VenueId = venueId, Status = status, SessionId = session_id });

            return Results.Ok(new { data = rows, total, page, page_size = pageSize });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("POS");

        // ═══════════════════════════════════════════════════════════════════════
        //  GET /api/venues/{venueId}/pos/orders/{orderId} — order detail
        // ═══════════════════════════════════════════════════════════════════════
        app.MapGet("/api/venues/{venueId}/pos/orders/{orderId}", async (
            Guid venueId,
            Guid orderId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var order = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, venue_id, session_id, member_id, station_id, items,
                       subtotal, tax, discount, total, payment_method,
                       status, notes, created_by, created_at, updated_at
                FROM pos_orders
                WHERE id = @OrderId AND venue_id = @VenueId
                """,
                new { OrderId = orderId, VenueId = venueId });

            return order is not null ? Results.Ok(order) : Results.NotFound(new { error = "Order not found." });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("POS");

        // ═══════════════════════════════════════════════════════════════════════
        //  PUT /api/venues/{venueId}/pos/orders/{orderId}/status — update status
        // ═══════════════════════════════════════════════════════════════════════
        app.MapPut("/api/venues/{venueId}/pos/orders/{orderId}/status", async (
            Guid venueId,
            Guid orderId,
            [FromBody] UpdatePOSOrderStatusRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<LiveHub> hubContext,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            if (string.IsNullOrWhiteSpace(req.Status))
                return Results.BadRequest(new { error = "Status is required." });

            var newStatus = req.Status.Trim().ToLowerInvariant();
            if (!ValidStatuses.Contains(newStatus))
                return Results.BadRequest(new { error = $"Invalid status. Must be one of: {string.Join(", ", ValidStatuses)}" });

            using var conn = db.CreateConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();

            try
            {
                var order = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT id, venue_id, station_id, status, items
                    FROM pos_orders
                    WHERE id = @OrderId AND venue_id = @VenueId
                    FOR UPDATE
                    """,
                    new { OrderId = orderId, VenueId = venueId }, tx);

                if (order is null)
                    return Results.NotFound(new { error = "Order not found." });

                var currentStatus = (string)order.status;

                // ── Validate status transitions ─────────────────────────────
                // Forward only: pending → preparing → ready → delivered
                // Cancel allowed from any non-terminal state
                if (!IsValidTransition(currentStatus, newStatus))
                    return Results.BadRequest(new
                    {
                        error = $"Cannot transition from '{currentStatus}' to '{newStatus}'.",
                        current_status = currentStatus,
                        requested_status = newStatus,
                    });

                // ── If cancelling, restore stock ────────────────────────────
                if (newStatus == "cancelled" && currentStatus != "cancelled")
                {
                    var itemsJson = (string)order.items;
                    var items = JsonSerializer.Deserialize<OrderLineItem[]>(itemsJson, JsonOpts);
                    if (items is not null)
                    {
                        foreach (var item in items)
                        {
                            // Only restore if the menu item has tracked stock (stock_count IS NOT NULL)
                            await conn.ExecuteAsync(
                                """
                                UPDATE venue_menu_items
                                SET stock_count = stock_count + @Qty
                                WHERE id = @ItemId AND venue_id = @VenueId
                                  AND stock_count IS NOT NULL
                                """,
                                new { Qty = item.Quantity, ItemId = item.ItemId, VenueId = venueId }, tx);
                        }
                    }
                }

                // ── Update status ───────────────────────────────────────────
                var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    UPDATE pos_orders
                    SET status = @NewStatus, updated_at = NOW()
                    WHERE id = @OrderId AND venue_id = @VenueId
                    RETURNING id, venue_id, session_id, member_id, station_id, items,
                              subtotal, tax, discount, total, payment_method,
                              status, notes, created_by, created_at, updated_at
                    """,
                    new { NewStatus = newStatus, OrderId = orderId, VenueId = venueId }, tx);

                tx.Commit();

                // ── Broadcast status change ─────────────────────────────────
                _ = hubContext.Clients
                    .Group(LiveHub.VenueGroup(venueId.ToString()))
                    .SendAsync(LiveHubEvents.POSOrderStatusChanged, new
                    {
                        order_id = orderId,
                        venue_id = venueId,
                        station_id = (string?)order.station_id,
                        previous_status = currentStatus,
                        status = newStatus,
                        updated_at = DateTimeOffset.UtcNow,
                    }, ct);

                return Results.Ok(updated);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        })
        .RequireAuthorization("Authenticated")
        .WithTags("POS");

        // ═══════════════════════════════════════════════════════════════════════
        //  GET /api/venues/{venueId}/pos/kitchen — kitchen display
        // ═══════════════════════════════════════════════════════════════════════
        app.MapGet("/api/venues/{venueId}/pos/kitchen", async (
            Guid venueId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var orders = await conn.QueryAsync<dynamic>(
                """
                SELECT o.id, o.venue_id, o.session_id, o.station_id, o.items,
                       o.subtotal, o.total, o.status, o.notes,
                       o.created_by, o.created_at, o.updated_at,
                       vs.label AS station_label
                FROM pos_orders o
                LEFT JOIN venue_stations vs
                    ON vs.venue_id = o.venue_id AND vs.station_id = o.station_id
                WHERE o.venue_id = @VenueId
                  AND o.status IN ('pending', 'preparing')
                ORDER BY
                    CASE o.status WHEN 'pending' THEN 0 ELSE 1 END,
                    o.created_at ASC
                """,
                new { VenueId = venueId });

            return Results.Ok(orders);
        })
        .RequireAuthorization("Authenticated")
        .WithTags("POS");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static bool IsValidTransition(string from, string to)
    {
        // Terminal states — no transitions out
        if (from is "delivered" or "cancelled")
            return false;

        // Cancel is always allowed from non-terminal states
        if (to == "cancelled")
            return true;

        // Forward-only transitions
        return (from, to) switch
        {
            ("pending", "preparing") => true,
            ("preparing", "ready") => true,
            ("ready", "delivered") => true,
            _ => false,
        };
    }

    private static async Task<bool> IsOwnerOrStaff(IDbConnectionFactory db, Guid? userId, Guid venueId)
    {
        if (userId is null) return false;
        using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM venues WHERE id = @VenueId AND owner_id = @UserId
                UNION ALL
                SELECT 1 FROM venue_staff WHERE venue_id = @VenueId AND user_id = @UserId AND status = 'active'
            )
            """,
            new { VenueId = venueId, UserId = userId });
    }

    // ── JSON deserialization options ────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    // ── Internal DTOs ───────────────────────────────────────────────────────────

    private sealed record MenuItemRow(
        Guid Id,
        string Name,
        string Category,
        decimal Price,
        bool IsAvailable,
        int? StockCount);

    private sealed record OrderLineItem(
        Guid ItemId,
        string Name,
        string Category,
        int Quantity,
        decimal UnitPrice,
        decimal Total);
}

// ── Request DTOs ────────────────────────────────────────────────────────────────

public sealed record CreatePOSOrderRequest(
    Guid? SessionId,
    Guid? MemberId,
    string? StationId,
    OrderItemRequest[] Items,
    string? Notes,
    string? PaymentMethod);

public sealed record OrderItemRequest(
    Guid ItemId,
    int Quantity);

public sealed record UpdatePOSOrderStatusRequest(
    string Status);
