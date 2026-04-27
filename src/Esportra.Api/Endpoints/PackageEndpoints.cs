using Dapper;
using Esportra.Contracts.Auth;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Domain: Hour Packages — venues sell hour bundles, members purchase and consume them.
/// </summary>
public static class PackageEndpoints
{
    public static void MapPackageEndpoints(this WebApplication app)
    {
        // ── GET /api/venues/{venueId}/packages — list venue packages ────────
        app.MapGet("/api/venues/{venueId}/packages", async (
            Guid venueId,
            bool all = false,
            IDbConnectionFactory db = null!,
            HttpContext ctx = null!,
            CancellationToken ct = default) =>
        {
            using var conn = db.CreateConnection();

            // If ?all=true, caller must be owner/staff to see inactive packages
            if (all)
            {
                var user = ctx.Items["UserContext"] as UserContext;
                if (user is null) return Results.Unauthorized();

                if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                    return Results.Forbid();

                var rows = await conn.QueryAsync<dynamic>(
                    """
                    SELECT id, venue_id, name, description, hours, price,
                           original_price, validity_days, is_active, sort_order,
                           created_at, updated_at
                    FROM venue_packages
                    WHERE venue_id = @VenueId
                    ORDER BY sort_order, created_at
                    """,
                    new { VenueId = venueId });

                return Results.Ok(rows);
            }

            // Public: active packages only
            var packages = await conn.QueryAsync<dynamic>(
                """
                SELECT id, venue_id, name, description, hours, price,
                       original_price, validity_days, sort_order
                FROM venue_packages
                WHERE venue_id = @VenueId AND is_active = true
                ORDER BY sort_order, price
                """,
                new { VenueId = venueId });

            return Results.Ok(packages);
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Packages");

        // ── POST /api/venues/{venueId}/packages — create package ────────────
        app.MapPost("/api/venues/{venueId}/packages", async (
            Guid venueId,
            CreatePackageRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "name is required" });
            if (req.Hours <= 0)
                return Results.BadRequest(new { error = "hours must be positive" });
            if (req.Price < 0)
                return Results.BadRequest(new { error = "price must be non-negative" });
            if (req.ValidityDays is not null && req.ValidityDays <= 0)
                return Results.BadRequest(new { error = "validity_days must be positive" });

            using var conn = db.CreateConnection();

            var id = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO venue_packages
                    (venue_id, name, description, hours, price, original_price, validity_days, is_active, sort_order)
                VALUES
                    (@VenueId, @Name, @Description, @Hours, @Price, @OriginalPrice, @ValidityDays, @IsActive, @SortOrder)
                RETURNING id
                """,
                new
                {
                    VenueId = venueId,
                    req.Name,
                    Description = req.Description ?? "",
                    req.Hours,
                    req.Price,
                    req.OriginalPrice,
                    ValidityDays = req.ValidityDays ?? 30,
                    IsActive = req.IsActive ?? true,
                    SortOrder = req.SortOrder ?? 0
                });

            return Results.Ok(new { id, name = req.Name });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Packages");

        // ── PUT /api/venues/{venueId}/packages/{packageId} — update ─────────
        app.MapPut("/api/venues/{venueId}/packages/{packageId}", async (
            Guid venueId,
            Guid packageId,
            UpdatePackageRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            if (req.Hours is not null && req.Hours <= 0)
                return Results.BadRequest(new { error = "hours must be positive" });
            if (req.Price is not null && req.Price < 0)
                return Results.BadRequest(new { error = "price must be non-negative" });
            if (req.ValidityDays is not null && req.ValidityDays <= 0)
                return Results.BadRequest(new { error = "validity_days must be positive" });

            using var conn = db.CreateConnection();

            var affected = await conn.ExecuteAsync(
                """
                UPDATE venue_packages
                SET name           = COALESCE(@Name, name),
                    description    = COALESCE(@Description, description),
                    hours          = COALESCE(@Hours, hours),
                    price          = COALESCE(@Price, price),
                    original_price = COALESCE(@OriginalPrice, original_price),
                    validity_days  = COALESCE(@ValidityDays, validity_days),
                    is_active      = COALESCE(@IsActive, is_active),
                    sort_order     = COALESCE(@SortOrder, sort_order),
                    updated_at     = NOW()
                WHERE id = @PackageId AND venue_id = @VenueId
                """,
                new
                {
                    PackageId = packageId,
                    VenueId = venueId,
                    req.Name,
                    req.Description,
                    req.Hours,
                    req.Price,
                    req.OriginalPrice,
                    req.ValidityDays,
                    req.IsActive,
                    req.SortOrder
                });

            return affected > 0
                ? Results.Ok(new { updated = true })
                : Results.NotFound(new { error = "Package not found" });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Packages");

        // ── DELETE /api/venues/{venueId}/packages/{packageId} — deactivate ──
        app.MapDelete("/api/venues/{venueId}/packages/{packageId}", async (
            Guid venueId,
            Guid packageId,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var affected = await conn.ExecuteAsync(
                """
                UPDATE venue_packages
                SET is_active = false, updated_at = NOW()
                WHERE id = @PackageId AND venue_id = @VenueId
                """,
                new { PackageId = packageId, VenueId = venueId });

            return affected > 0
                ? Results.Ok(new { deactivated = true })
                : Results.NotFound(new { error = "Package not found" });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Packages");

        // ── POST /api/venues/{venueId}/members/{memberId}/purchase-package ──
        app.MapPost("/api/venues/{venueId}/members/{memberId}/purchase-package", async (
            Guid venueId,
            Guid memberId,
            PurchasePackageRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            if (req.PackageId == Guid.Empty)
                return Results.BadRequest(new { error = "package_id is required" });

            using var conn = db.CreateConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();

            try
            {
                // Fetch the package
                var package = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    """
                    SELECT id, venue_id, hours, price, validity_days, is_active
                    FROM venue_packages
                    WHERE id = @PackageId AND venue_id = @VenueId
                    """,
                    new { PackageId = req.PackageId, VenueId = venueId }, tx);

                if (package is null)
                    return Results.NotFound(new { error = "Package not found" });

                if (!(bool)package.is_active)
                    return Results.BadRequest(new { error = "Package is no longer available" });

                // Verify member exists in this venue
                var memberExists = await conn.QuerySingleAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM members WHERE id = @MemberId AND venue_id = @VenueId)",
                    new { MemberId = memberId, VenueId = venueId }, tx);

                if (!memberExists)
                    return Results.NotFound(new { error = "Member not found" });

                // If paying from balance, atomically check and deduct
                if (string.Equals(req.PaymentMethod, "balance", StringComparison.OrdinalIgnoreCase))
                {
                    var rowsAffected = await conn.ExecuteAsync(
                        """
                        UPDATE members
                        SET balance = balance - @Price, updated_at = NOW()
                        WHERE id = @MemberId AND venue_id = @VenueId AND balance >= @Price
                        """,
                        new { MemberId = memberId, VenueId = venueId, Price = (decimal)package.price }, tx);

                    if (rowsAffected == 0)
                        return Results.BadRequest(new { error = "Insufficient balance" });
                }

                // Create the member package
                var memberPackageId = await conn.QuerySingleAsync<Guid>(
                    """
                    INSERT INTO member_packages
                        (venue_id, member_id, package_id, hours_total, hours_remaining, expires_at)
                    VALUES
                        (@VenueId, @MemberId, @PackageId, @HoursTotal, @HoursRemaining, NOW() + (@ValidityDays || ' days')::INTERVAL)
                    RETURNING id
                    """,
                    new
                    {
                        VenueId = venueId,
                        MemberId = memberId,
                        PackageId = req.PackageId,
                        HoursTotal = (decimal)package.hours,
                        HoursRemaining = (decimal)package.hours,
                        ValidityDays = (int)package.validity_days
                    }, tx);

                tx.Commit();

                return Results.Ok(new
                {
                    id = memberPackageId,
                    package_id = req.PackageId,
                    hours_total = (decimal)package.hours,
                    hours_remaining = (decimal)package.hours,
                    payment_method = req.PaymentMethod ?? "cash",
                    price = (decimal)package.price
                });
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Packages");

        // ── GET /api/venues/{venueId}/members/{memberId}/packages ───────────
        app.MapGet("/api/venues/{venueId}/members/{memberId}/packages", async (
            Guid venueId,
            Guid memberId,
            bool includeExpired = false,
            IDbConnectionFactory db = null!,
            HttpContext ctx = null!,
            CancellationToken ct = default) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var statusFilter = includeExpired ? "" : "AND mp.status = 'active' AND mp.expires_at > NOW()";

            var rows = await conn.QueryAsync<dynamic>(
                $"""
                SELECT mp.id, mp.package_id, mp.hours_total, mp.hours_remaining,
                       mp.purchased_at, mp.expires_at, mp.status,
                       vp.name AS package_name, vp.price AS package_price
                FROM member_packages mp
                JOIN venue_packages vp ON vp.id = mp.package_id
                WHERE mp.venue_id = @VenueId
                  AND mp.member_id = @MemberId
                  {statusFilter}
                ORDER BY mp.purchased_at DESC
                """,
                new { VenueId = venueId, MemberId = memberId });

            return Results.Ok(rows);
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Packages");

        // ── POST /api/venues/{venueId}/members/{memberId}/packages/{memberPackageId}/deduct
        app.MapPost("/api/venues/{venueId}/members/{memberId}/packages/{memberPackageId}/deduct", async (
            Guid venueId,
            Guid memberId,
            Guid memberPackageId,
            DeductHoursRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            if (req.Hours <= 0)
                return Results.BadRequest(new { error = "hours must be positive" });

            using var conn = db.CreateConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();

            try
            {
                // Atomically deduct hours with guard
                var updated = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    """
                    UPDATE member_packages
                    SET hours_remaining = hours_remaining - @Hours,
                        status = CASE WHEN hours_remaining - @Hours <= 0 THEN 'depleted' ELSE status END
                    WHERE id = @Id AND venue_id = @VenueId AND member_id = @MemberId
                      AND status = 'active' AND expires_at > NOW()
                      AND hours_remaining >= @Hours
                    RETURNING hours_remaining, status
                    """,
                    new { Id = memberPackageId, VenueId = venueId, MemberId = memberId, Hours = req.Hours }, tx);

                if (updated is null)
                {
                    // Check why it failed
                    var mp = await conn.QueryFirstOrDefaultAsync<dynamic>(
                        "SELECT status, expires_at, hours_remaining FROM member_packages WHERE id = @Id AND venue_id = @VenueId AND member_id = @MemberId",
                        new { Id = memberPackageId, VenueId = venueId, MemberId = memberId }, tx);

                    if (mp is null)
                        return Results.NotFound(new { error = "Member package not found" });
                    if ((string)mp.status != "active")
                        return Results.BadRequest(new { error = $"Package is {mp.status}" });
                    if ((DateTime)mp.expires_at < DateTime.UtcNow)
                    {
                        await conn.ExecuteAsync("UPDATE member_packages SET status = 'expired' WHERE id = @Id AND venue_id = @VenueId AND member_id = @MemberId", new { Id = memberPackageId, VenueId = venueId, MemberId = memberId }, tx);
                        tx.Commit();
                        return Results.BadRequest(new { error = "Package has expired" });
                    }
                    return Results.BadRequest(new { error = $"Only {(decimal)mp.hours_remaining} hours remaining" });
                }

                tx.Commit();

                return Results.Ok(new
                {
                    hours_deducted = req.Hours,
                    hours_remaining = (decimal)updated.hours_remaining,
                    status = (string)updated.status
                });
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Packages");
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
}

// ── Request DTOs ────────────────────────────────────────────────────────────
public record CreatePackageRequest(
    string Name,
    decimal Hours,
    decimal Price,
    string? Description = null,
    decimal? OriginalPrice = null,
    int? ValidityDays = null,
    bool? IsActive = null,
    int? SortOrder = null);

public record UpdatePackageRequest(
    string? Name = null,
    string? Description = null,
    decimal? Hours = null,
    decimal? Price = null,
    decimal? OriginalPrice = null,
    int? ValidityDays = null,
    bool? IsActive = null,
    int? SortOrder = null);

public record PurchasePackageRequest(
    Guid PackageId,
    string? PaymentMethod = null);

public record DeductHoursRequest(decimal Hours);
