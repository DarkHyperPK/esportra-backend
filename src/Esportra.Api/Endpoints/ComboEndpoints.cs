using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Domain: Venue Menu Items &amp; Combos — F&amp;B menu management and
/// bundled combo deals (gaming + food/drink) for venues.
/// </summary>
public static class ComboEndpoints
{
    public static void MapComboEndpoints(this WebApplication app)
    {
        // ═══════════════════════════════════════════════════════════════════════
        //  MENU ITEMS
        // ═══════════════════════════════════════════════════════════════════════

        // ── GET /api/venues/{venueId}/menu — menu items ──────────────────────
        // ?staff=true returns ALL items (including unavailable) for management UI.
        // Default returns only available items (public/gamer view).
        app.MapGet("/api/venues/{venueId}/menu", async (
            Guid                 venueId,
            [FromQuery] bool     staff = false,
            HttpContext          ctx = null!,
            IDbConnectionFactory db  = null!,
            CancellationToken    ct  = default) =>
        {
            using var conn = db.CreateConnection();

            if (staff)
            {
                var userCtx = ctx.Items["UserContext"] as UserContext;
                if (userCtx is null) return Results.Unauthorized();

                var staffCheck = await conn.QuerySingleOrDefaultAsync<int>(
                    "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND accepted_at IS NOT NULL LIMIT 1",
                    new { userId = userCtx.UserIdGuid, venueId });
                if (staffCheck == 0) return Results.Unauthorized();

                var all = await conn.QueryAsync<dynamic>(
                    """
                    SELECT id, venue_id, name, category, price,
                           is_available, sort_order, created_at, updated_at
                    FROM venue_menu_items
                    WHERE venue_id = @venueId
                    ORDER BY category, sort_order
                    LIMIT 200
                    """,
                    new { venueId });
                return Results.Ok(all);
            }

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, venue_id, name, category, price,
                       is_available, sort_order, created_at, updated_at
                FROM venue_menu_items
                WHERE venue_id     = @venueId
                  AND is_available = true
                ORDER BY category, sort_order
                LIMIT 200
                """,
                new { venueId });

            return Results.Ok(rows);
        });

        // ── POST /api/venues/{venueId}/menu — create menu item ──────────────
        app.MapPost("/api/venues/{venueId}/menu", async (
            Guid                             venueId,
            [FromBody] CreateMenuItemRequest  req,
            HttpContext                       ctx,
            IDbConnectionFactory             db,
            CancellationToken                ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });
            if (string.IsNullOrWhiteSpace(req.Category))
                return Results.BadRequest(new { error = "Category is required." });
            if (req.Price < 0)
                return Results.BadRequest(new { error = "Price must be zero or greater." });

            using var conn = db.CreateConnection();

            // Venue staff gate
            var staffCheck = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND accepted_at IS NOT NULL LIMIT 1",
                new { userId = userCtx.UserIdGuid, venueId });
            if (staffCheck == 0) return Results.Unauthorized();

            var row = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO venue_menu_items (venue_id, name, category, price)
                VALUES (@venueId, @name, @category, @price)
                RETURNING id, venue_id, name, category, price,
                          is_available, sort_order, created_at, updated_at
                """,
                new
                {
                    venueId,
                    name     = req.Name.Trim(),
                    category = req.Category.Trim(),
                    price    = req.Price
                });

            return Results.Created($"/api/venues/{venueId}/menu/{row.id}", row);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/venues/{venueId}/menu/{id} — update menu item ──────────
        app.MapPut("/api/venues/{venueId}/menu/{id}", async (
            Guid                              venueId,
            Guid                              id,
            [FromBody] UpdateMenuItemRequest   req,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            CancellationToken                 ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });
            if (string.IsNullOrWhiteSpace(req.Category))
                return Results.BadRequest(new { error = "Category is required." });
            if (req.Price < 0)
                return Results.BadRequest(new { error = "Price must be zero or greater." });

            using var conn = db.CreateConnection();

            // Venue staff gate
            var staffCheck = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND accepted_at IS NOT NULL LIMIT 1",
                new { userId = userCtx.UserIdGuid, venueId });
            if (staffCheck == 0) return Results.Unauthorized();

            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE venue_menu_items
                SET name         = @name,
                    category     = @category,
                    price        = @price,
                    is_available = @isAvailable,
                    sort_order   = @sortOrder
                WHERE id = @id AND venue_id = @venueId
                RETURNING id, venue_id, name, category, price,
                          is_available, sort_order, created_at, updated_at
                """,
                new
                {
                    id,
                    venueId,
                    name        = req.Name.Trim(),
                    category    = req.Category.Trim(),
                    price       = req.Price,
                    isAvailable = req.IsAvailable,
                    sortOrder   = req.SortOrder
                });

            return row is not null ? Results.Ok(row) : Results.NotFound();
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/venues/{venueId}/menu/{id} — owner only ─────────────
        app.MapDelete("/api/venues/{venueId}/menu/{id}", async (
            Guid                 venueId,
            Guid                 id,
            HttpContext          ctx = null!,
            IDbConnectionFactory db  = null!,
            CancellationToken    ct  = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Venue owner gate (role = 'owner')
            var ownerCheck = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND role = 'owner' AND accepted_at IS NOT NULL LIMIT 1",
                new { userId = userCtx.UserIdGuid, venueId });
            if (ownerCheck == 0) return Results.Unauthorized();

            // Check if any active combos reference this menu item via JSONB
            var comboRef = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM venue_combos,
                    LATERAL jsonb_array_elements(items) AS item
                    WHERE venue_id = @venueId AND is_active = true
                      AND (item->>'menu_item_id')::uuid = @id
                )
                """,
                new { venueId, id });
            if (comboRef)
                return Results.Conflict(new { error = "Cannot delete: this item is used in one or more active combos. Deactivate the combos first." });

            var deleted = await conn.ExecuteAsync(
                "DELETE FROM venue_menu_items WHERE id = @id AND venue_id = @venueId",
                new { id, venueId });

            return deleted > 0 ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization("Authenticated");

        // ═══════════════════════════════════════════════════════════════════════
        //  COMBOS
        // ═══════════════════════════════════════════════════════════════════════

        // ── GET /api/venues/{venueId}/combos — combos ────────────────────────
        // ?staff=true returns ALL combos (including inactive) for management UI.
        // Default returns only active combos (public/gamer view).
        app.MapGet("/api/venues/{venueId}/combos", async (
            Guid                 venueId,
            [FromQuery] bool     staff = false,
            HttpContext          ctx = null!,
            IDbConnectionFactory db  = null!,
            CancellationToken    ct  = default) =>
        {
            using var conn = db.CreateConnection();

            if (staff)
            {
                var userCtx = ctx.Items["UserContext"] as UserContext;
                if (userCtx is null) return Results.Unauthorized();

                var staffCheck = await conn.QuerySingleOrDefaultAsync<int>(
                    "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND accepted_at IS NOT NULL LIMIT 1",
                    new { userId = userCtx.UserIdGuid, venueId });
                if (staffCheck == 0) return Results.Unauthorized();

                var all = await conn.QueryAsync<dynamic>(
                    """
                    SELECT id, venue_id, name, description, items,
                           total_price, original_price,
                           (original_price - total_price) AS savings,
                           is_active, sort_order, created_at, updated_at
                    FROM venue_combos
                    WHERE venue_id = @venueId
                    ORDER BY sort_order
                    LIMIT 100
                    """,
                    new { venueId });
                return Results.Ok(all);
            }

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, venue_id, name, description, items,
                       total_price, original_price,
                       (original_price - total_price) AS savings,
                       is_active, sort_order, created_at, updated_at
                FROM venue_combos
                WHERE venue_id  = @venueId
                  AND is_active = true
                ORDER BY sort_order
                LIMIT 100
                """,
                new { venueId });

            return Results.Ok(rows);
        });

        // ── POST /api/venues/{venueId}/combos — create combo ────────────────
        app.MapPost("/api/venues/{venueId}/combos", async (
            Guid                           venueId,
            [FromBody] CreateComboRequest   req,
            HttpContext                     ctx,
            IDbConnectionFactory           db,
            CancellationToken              ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });
            if (req.TotalPrice < 0)
                return Results.BadRequest(new { error = "Total price must be zero or greater." });
            if (req.OriginalPrice < 0)
                return Results.BadRequest(new { error = "Original price must be zero or greater." });

            // Validate items JSON
            if (!string.IsNullOrWhiteSpace(req.Items))
            {
                try
                {
                    using var doc = JsonDocument.Parse(req.Items);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                        return Results.BadRequest(new { error = "Items must be a JSON array." });
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "Invalid items JSON." });
                }
            }

            using var conn = db.CreateConnection();

            // Venue staff gate
            var staffCheck = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND accepted_at IS NOT NULL LIMIT 1",
                new { userId = userCtx.UserIdGuid, venueId });
            if (staffCheck == 0) return Results.Unauthorized();

            var row = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO venue_combos
                    (venue_id, name, description, items, total_price, original_price)
                VALUES
                    (@venueId, @name, @description, @items::jsonb, @totalPrice, @originalPrice)
                RETURNING id, venue_id, name, description, items,
                          total_price, original_price,
                          (original_price - total_price) AS savings,
                          is_active, sort_order, created_at, updated_at
                """,
                new
                {
                    venueId,
                    name          = req.Name.Trim(),
                    description   = req.Description?.Trim() ?? "",
                    items         = req.Items ?? "[]",
                    totalPrice    = req.TotalPrice,
                    originalPrice = req.OriginalPrice
                });

            return Results.Created($"/api/venues/{venueId}/combos/{row.id}", row);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/venues/{venueId}/combos/{id} — update combo ────────────
        app.MapPut("/api/venues/{venueId}/combos/{id}", async (
            Guid                           venueId,
            Guid                           id,
            [FromBody] UpdateComboRequest   req,
            HttpContext                     ctx,
            IDbConnectionFactory           db,
            CancellationToken              ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });
            if (req.TotalPrice < 0)
                return Results.BadRequest(new { error = "Total price must be zero or greater." });
            if (req.OriginalPrice < 0)
                return Results.BadRequest(new { error = "Original price must be zero or greater." });

            // Validate items JSON
            if (!string.IsNullOrWhiteSpace(req.Items))
            {
                try
                {
                    using var doc = JsonDocument.Parse(req.Items);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                        return Results.BadRequest(new { error = "Items must be a JSON array." });
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "Invalid items JSON." });
                }
            }

            using var conn = db.CreateConnection();

            // Venue staff gate
            var staffCheck = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND accepted_at IS NOT NULL LIMIT 1",
                new { userId = userCtx.UserIdGuid, venueId });
            if (staffCheck == 0) return Results.Unauthorized();

            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE venue_combos
                SET name           = @name,
                    description    = @description,
                    items          = @items::jsonb,
                    total_price    = @totalPrice,
                    original_price = @originalPrice,
                    is_active      = @isActive,
                    sort_order     = @sortOrder
                WHERE id = @id AND venue_id = @venueId
                RETURNING id, venue_id, name, description, items,
                          total_price, original_price,
                          (original_price - total_price) AS savings,
                          is_active, sort_order, created_at, updated_at
                """,
                new
                {
                    id,
                    venueId,
                    name          = req.Name.Trim(),
                    description   = req.Description?.Trim() ?? "",
                    items         = req.Items ?? "[]",
                    totalPrice    = req.TotalPrice,
                    originalPrice = req.OriginalPrice,
                    isActive      = req.IsActive,
                    sortOrder     = req.SortOrder
                });

            return row is not null ? Results.Ok(row) : Results.NotFound();
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/venues/{venueId}/combos/{id} — owner only ───────────
        app.MapDelete("/api/venues/{venueId}/combos/{id}", async (
            Guid                 venueId,
            Guid                 id,
            HttpContext          ctx = null!,
            IDbConnectionFactory db  = null!,
            CancellationToken    ct  = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Venue owner gate (role = 'owner')
            var ownerCheck = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND role = 'owner' AND accepted_at IS NOT NULL LIMIT 1",
                new { userId = userCtx.UserIdGuid, venueId });
            if (ownerCheck == 0) return Results.Unauthorized();

            var deleted = await conn.ExecuteAsync(
                "DELETE FROM venue_combos WHERE id = @id AND venue_id = @venueId",
                new { id, venueId });

            return deleted > 0 ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization("Authenticated");
    }

    // ── Request DTOs ────────────────────────────────────────────────────────────

    private sealed record CreateMenuItemRequest(
        string  Name,
        string  Category,
        decimal Price);

    private sealed record UpdateMenuItemRequest(
        string  Name,
        string  Category,
        decimal Price,
        bool    IsAvailable,
        int     SortOrder);

    private sealed record CreateComboRequest(
        string  Name,
        string? Description,
        string? Items,
        decimal TotalPrice,
        decimal OriginalPrice);

    private sealed record UpdateComboRequest(
        string  Name,
        string? Description,
        string? Items,
        decimal TotalPrice,
        decimal OriginalPrice,
        bool    IsActive,
        int     SortOrder);
}
