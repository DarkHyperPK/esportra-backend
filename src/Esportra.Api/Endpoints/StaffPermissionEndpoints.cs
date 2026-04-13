using System.Collections.Concurrent;
using Dapper;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class StaffPermissionEndpoints
{
    // ── Rate limiting for PIN login ─────────────────────────────────────────
    private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> PinAttempts = new();
    private const int MaxPinAttempts = 5;
    private static readonly TimeSpan PinWindow = TimeSpan.FromMinutes(5);

    // Periodic cleanup of expired rate limit entries
    private static readonly Timer PinCleanupTimer = new(_ =>
    {
        var cutoff = DateTime.UtcNow - PinWindow;
        foreach (var kvp in PinAttempts)
            if (kvp.Value.WindowStart < cutoff)
                PinAttempts.TryRemove(kvp.Key, out (int, DateTime) _);
    }, null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));

    // ── All known permissions (source of truth) ─────────────────────────────
    private static readonly string[] AllPermissions =
    [
        "stations.view", "stations.control",
        "sessions.start", "sessions.end", "sessions.refund",
        "members.view", "members.create", "members.ban", "members.topup",
        "bookings.view", "bookings.create", "bookings.cancel",
        "pos.orders", "pos.catalog",
        "analytics.view", "analytics.export",
        "staff.manage",
        "settings.edit"
    ];

    // ── Default permissions per role (used when venue has none configured) ───
    private static readonly Dictionary<string, string[]> DefaultPermissions = new()
    {
        ["manager"] = AllPermissions,
        ["cashier"] = ["stations.view", "sessions.start", "sessions.end", "members.view", "members.topup", "bookings.view", "bookings.create", "pos.orders", "pos.catalog"],
        ["staff"] = ["stations.view", "sessions.start", "sessions.end", "members.view", "bookings.view", "bookings.create", "pos.orders"],
        ["technician"] = ["stations.view", "stations.control", "sessions.start", "sessions.end", "bookings.view"],
    };

    public static void MapStaffPermissionEndpoints(this WebApplication app)
    {
        // ── 1. GET /api/venues/{venueId}/permissions ────────────────────────
        // Returns the full permission matrix: role → permission[]
        app.MapGet("/api/venues/{venueId}/permissions", async (
            Guid venueId,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<PermissionRow>(
                """
                SELECT role, permission
                FROM staff_permissions
                WHERE venue_id = @VenueId
                ORDER BY role, permission
                """,
                new { VenueId = venueId });

            var matrix = rows
                .GroupBy(r => r.Role)
                .ToDictionary(g => g.Key, g => g.Select(r => r.Permission).ToArray());

            // If no permissions configured yet, return defaults
            if (matrix.Count == 0)
                matrix = DefaultPermissions.ToDictionary(kv => kv.Key, kv => kv.Value);

            return Results.Ok(new
            {
                permissions = matrix,
                available_permissions = AllPermissions,
            });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Staff Permissions");

        // ── 2. PUT /api/venues/{venueId}/permissions ────────────────────────
        // Bulk update: replace all permissions for a given role in this venue.
        // Body: { role: "staff", permissions: ["stations.view", ...] }
        app.MapPut("/api/venues/{venueId}/permissions", async (
            Guid venueId,
            [FromBody] UpdatePermissionsRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOfVenue(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            // Validate role
            if (string.IsNullOrWhiteSpace(req.Role) || req.Role == "owner")
                return Results.BadRequest(new { error = "Invalid role. Cannot modify owner permissions." });

            if (req.Role is not ("manager" or "cashier" or "technician" or "staff"))
                return Results.BadRequest(new { error = "Role must be 'manager', 'cashier', 'technician', or 'staff'." });

            // Validate permissions — only allow known values
            var invalid = (req.Permissions ?? []).Where(p => !AllPermissions.Contains(p)).ToArray();
            if (invalid.Length > 0)
                return Results.BadRequest(new { error = $"Unknown permissions: {string.Join(", ", invalid)}" });

            using var conn = db.CreateConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();

            try
            {
                // Delete existing permissions for this role in this venue
                await conn.ExecuteAsync(
                    "DELETE FROM staff_permissions WHERE venue_id = @VenueId AND role = @Role",
                    new { VenueId = venueId, Role = req.Role }, tx);

                // Insert new permissions
                if (req.Permissions is { Length: > 0 })
                {
                    foreach (var perm in req.Permissions.Distinct())
                    {
                        await conn.ExecuteAsync(
                            """
                            INSERT INTO staff_permissions (venue_id, role, permission)
                            VALUES (@VenueId, @Role, @Permission)
                            ON CONFLICT (venue_id, role, permission) DO NOTHING
                            """,
                            new { VenueId = venueId, Role = req.Role, Permission = perm }, tx);
                    }
                }

                tx.Commit();

                return Results.Ok(new { updated = true, role = req.Role, permission_count = req.Permissions?.Distinct().Count() ?? 0 });
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Staff Permissions");

        // ── 3. POST /api/venues/{venueId}/staff/{staffId}/set-pin ───────────
        // Set a 4–6 digit PIN for a staff member. Owner/manager can set for others.
        app.MapPost("/api/venues/{venueId}/staff/{staffId}/set-pin", async (
            Guid venueId,
            Guid staffId,
            [FromBody] SetPinRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            // Must be owner or manager
            if (!await IsOwnerOrManager(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            // Validate PIN: 4–6 digits only
            if (string.IsNullOrWhiteSpace(req.Pin) || req.Pin.Length < 4 || req.Pin.Length > 6 || !req.Pin.All(char.IsDigit))
                return Results.BadRequest(new { error = "PIN must be 4–6 digits." });

            using var conn = db.CreateConnection();

            // Verify staff exists in this venue
            var staffExists = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM venue_staff WHERE id = @StaffId AND venue_id = @VenueId)",
                new { StaffId = staffId, VenueId = venueId });
            if (!staffExists)
                return Results.NotFound(new { error = "Staff member not found." });

            // Check PIN uniqueness within venue (no two staff share a PIN)
            var pinHash = BCrypt.Net.BCrypt.HashPassword(req.Pin);

            // To check uniqueness, we need to verify against all existing PINs in the venue
            var existingHashes = await conn.QueryAsync<(Guid Id, string PinCodeHash)>(
                "SELECT id, pin_code_hash FROM venue_staff WHERE venue_id = @VenueId AND pin_code_hash IS NOT NULL AND id != @StaffId",
                new { VenueId = venueId, StaffId = staffId });

            foreach (var existing in existingHashes)
            {
                if (BCrypt.Net.BCrypt.Verify(req.Pin, existing.PinCodeHash))
                    return Results.Conflict(new { error = "This PIN is already in use by another staff member." });
            }

            await conn.ExecuteAsync(
                "UPDATE venue_staff SET pin_code_hash = @Hash WHERE id = @StaffId AND venue_id = @VenueId",
                new { Hash = pinHash, StaffId = staffId, VenueId = venueId });

            return Results.Ok(new { success = true });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Staff Permissions");

        // ── 4. POST /api/auth/staff-pin-login ───────────────────────────────
        // Kiosk/POS PIN login: returns staff context + permissions (no JWT required).
        app.MapPost("/api/auth/staff-pin-login", async (
            [FromBody] StaffPinLoginRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (req.VenueId == Guid.Empty || string.IsNullOrWhiteSpace(req.Pin))
                return Results.BadRequest(new { error = "venue_id and pin are required." });

            if (req.Pin.Length < 4 || req.Pin.Length > 6 || !req.Pin.All(char.IsDigit))
                return Results.BadRequest(new { error = "Invalid PIN format." });

            // Rate limiting: max 5 attempts per venue+IP per 5 minutes (atomic)
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var rateLimitKey = $"{req.VenueId}:{ip}";

            var current = PinAttempts.AddOrUpdate(rateLimitKey,
                _ => (1, DateTime.UtcNow),
                (_, existing) => DateTime.UtcNow - existing.WindowStart < PinWindow
                    ? (existing.Count + 1, existing.WindowStart)
                    : (1, DateTime.UtcNow));

            if (current.Count > MaxPinAttempts)
                return Results.Json(new { error = "Too many PIN attempts. Try again later." }, statusCode: 429);

            using var conn = db.CreateConnection();

            // Fetch all active staff with PINs for this venue
            var candidates = await conn.QueryAsync<StaffPinCandidate>(
                """
                SELECT vs.id, vs.user_id, vs.role, vs.pin_code_hash,
                       u.raw_user_meta_data->>'display_name' AS display_name,
                       u.email
                FROM venue_staff vs
                JOIN auth.users u ON u.id = vs.user_id
                WHERE vs.venue_id = @VenueId
                  AND vs.pin_code_hash IS NOT NULL
                  AND vs.status = 'active'
                """,
                new { req.VenueId });

            // Try to match the PIN
            StaffPinCandidate? matched = null;
            foreach (var candidate in candidates)
            {
                if (BCrypt.Net.BCrypt.Verify(req.Pin, candidate.PinCodeHash))
                {
                    matched = candidate;
                    break;
                }
            }

            if (matched is null)
                return Results.Unauthorized();

            // Successful login — clear attempts
            PinAttempts.TryRemove(rateLimitKey, out _);

            // Fetch permissions for this staff's role
            var permissions = (await conn.QueryAsync<string>(
                "SELECT permission FROM staff_permissions WHERE venue_id = @VenueId AND role = @Role",
                new { req.VenueId, matched.Role })).ToArray();

            // If no custom permissions, use defaults
            if (permissions.Length == 0 && DefaultPermissions.TryGetValue(matched.Role, out var defaults))
                permissions = defaults;

            return Results.Ok(new
            {
                staff_id = matched.Id,
                user_id = matched.UserId,
                display_name = matched.DisplayName,
                email = matched.Email,
                role = matched.Role,
                venue_id = req.VenueId,
                permissions,
            });
        })
        .WithTags("Staff Permissions");

        // ── 5. POST /api/venues/{venueId}/staff/clock-in ────────────────────
        app.MapPost("/api/venues/{venueId}/staff/clock-in", async (
            Guid venueId,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Resolve venue_staff record for this user
            var staff = await conn.QueryFirstOrDefaultAsync<StaffRecord>(
                """
                SELECT vs.id, vs.role,
                       u.raw_user_meta_data->>'display_name' AS display_name
                FROM venue_staff vs
                JOIN auth.users u ON u.id = vs.user_id
                WHERE vs.venue_id = @VenueId AND vs.user_id = @UserId AND vs.status = 'active'
                """,
                new { VenueId = venueId, UserId = user.UserIdGuid });

            if (staff is null)
                return Results.Forbid();

            // Check if already clocked in (prevent double clock-in)
            var alreadyClockedIn = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM staff_shifts WHERE venue_id = @VenueId AND staff_id = @StaffId AND clocked_out IS NULL)",
                new { VenueId = venueId, StaffId = staff.Id });

            if (alreadyClockedIn)
                return Results.Conflict(new { error = "Already clocked in. Clock out first." });

            var shiftId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO staff_shifts (venue_id, staff_id, staff_name, clocked_in)
                VALUES (@VenueId, @StaffId, @StaffName, NOW())
                RETURNING id
                """,
                new { VenueId = venueId, StaffId = staff.Id, StaffName = staff.DisplayName });

            return Results.Ok(new
            {
                shift_id = shiftId,
                staff_id = staff.Id,
                staff_name = staff.DisplayName,
                clocked_in = DateTime.UtcNow,
            });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Staff Permissions");

        // ── 6. POST /api/venues/{venueId}/staff/clock-out ───────────────────
        app.MapPost("/api/venues/{venueId}/staff/clock-out", async (
            Guid venueId,
            [FromBody] ClockOutRequest? req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Resolve venue_staff record
            var staffId = await conn.QueryFirstOrDefaultAsync<Guid?>(
                "SELECT id FROM venue_staff WHERE venue_id = @VenueId AND user_id = @UserId AND status = 'active'",
                new { VenueId = venueId, UserId = user.UserIdGuid });

            if (staffId is null)
                return Results.Forbid();

            // Find the open shift
            var shift = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT id, clocked_in FROM staff_shifts WHERE venue_id = @VenueId AND staff_id = @StaffId AND clocked_out IS NULL",
                new { VenueId = venueId, StaffId = staffId });

            if (shift is null)
                return Results.NotFound(new { error = "No active shift found. Clock in first." });

            // Clock out + calculate total minutes
            var updated = await conn.QuerySingleAsync<dynamic>(
                """
                UPDATE staff_shifts
                SET clocked_out   = NOW(),
                    total_minutes = EXTRACT(EPOCH FROM (NOW() - clocked_in)) / 60.0,
                    notes         = @Notes
                WHERE id = @ShiftId
                RETURNING id, clocked_in, clocked_out, total_minutes, notes
                """,
                new { ShiftId = (Guid)shift.id, Notes = req?.Notes });

            return Results.Ok(new
            {
                shift_id = (Guid)updated.id,
                clocked_in = (DateTime)updated.clocked_in,
                clocked_out = (DateTime)updated.clocked_out,
                total_minutes = (decimal)updated.total_minutes,
                notes = (string?)updated.notes,
            });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Staff Permissions");

        // ── 7. GET /api/venues/{venueId}/staff/shifts ───────────────────────
        // Paginated shift history with optional date + staff filters
        app.MapGet("/api/venues/{venueId}/staff/shifts", async (
            Guid venueId,
            DateTime? from,
            DateTime? to,
            Guid? staff_id,
            int page = 1,
            int pageSize = 25,
            IDbConnectionFactory db = null!,
            HttpContext ctx = null!,
            CancellationToken ct = default) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var offset = (page - 1) * pageSize;

            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT ss.id, ss.staff_id, ss.staff_name, ss.clocked_in, ss.clocked_out,
                       ss.total_minutes, ss.notes, ss.created_at
                FROM staff_shifts ss
                WHERE ss.venue_id = @VenueId
                  AND (@From IS NULL OR ss.clocked_in >= @From)
                  AND (@To   IS NULL OR ss.clocked_in <= @To)
                  AND (@StaffId IS NULL OR ss.staff_id = @StaffId)
                ORDER BY ss.clocked_in DESC
                LIMIT @Limit OFFSET @Offset
                """,
                new { VenueId = venueId, From = from, To = to, StaffId = staff_id, Limit = pageSize, Offset = offset });

            var total = await conn.QuerySingleAsync<int>(
                """
                SELECT COUNT(*)
                FROM staff_shifts
                WHERE venue_id = @VenueId
                  AND (@From IS NULL OR clocked_in >= @From)
                  AND (@To   IS NULL OR clocked_in <= @To)
                  AND (@StaffId IS NULL OR staff_id = @StaffId)
                """,
                new { VenueId = venueId, From = from, To = to, StaffId = staff_id });

            return Results.Ok(new { data = rows, total, page, page_size = pageSize });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Staff Permissions");

        // ── 8. GET /api/venues/{venueId}/staff/active-shifts ────────────────
        // Currently clocked-in staff
        app.MapGet("/api/venues/{venueId}/staff/active-shifts", async (
            Guid venueId,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT ss.id AS shift_id, ss.staff_id, ss.staff_name,
                       ss.clocked_in,
                       EXTRACT(EPOCH FROM (NOW() - ss.clocked_in)) / 60.0 AS minutes_elapsed,
                       vs.role
                FROM staff_shifts ss
                JOIN venue_staff vs ON vs.id = ss.staff_id
                WHERE ss.venue_id = @VenueId AND ss.clocked_out IS NULL
                ORDER BY ss.clocked_in ASC
                """,
                new { VenueId = venueId });

            return Results.Ok(rows);
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Staff Permissions");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

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

    private static async Task<bool> IsOwnerOfVenue(IDbConnectionFactory db, Guid? userId, Guid venueId)
    {
        if (userId is null) return false;
        using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM venues WHERE id = @VenueId AND owner_id = @UserId)",
            new { VenueId = venueId, UserId = userId });
    }

    private static async Task<bool> IsOwnerOrManager(IDbConnectionFactory db, Guid? userId, Guid venueId)
    {
        if (userId is null) return false;
        using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM venues WHERE id = @VenueId AND owner_id = @UserId
                UNION ALL
                SELECT 1 FROM venue_staff
                WHERE venue_id = @VenueId AND user_id = @UserId AND status = 'active'
                  AND role IN ('owner', 'manager')
            )
            """,
            new { VenueId = venueId, UserId = userId });
    }

    // ── Request/Response DTOs ───────────────────────────────────────────────

    private sealed record UpdatePermissionsRequest(string Role, string[]? Permissions);
    private sealed record SetPinRequest(string Pin);
    private sealed record StaffPinLoginRequest(Guid VenueId, string Pin);
    private sealed record ClockOutRequest(string? Notes = null);

    private sealed record PermissionRow(string Role, string Permission);
    private sealed record StaffPinCandidate(Guid Id, Guid UserId, string Role, string PinCodeHash, string? DisplayName, string? Email);
    private sealed record StaffRecord(Guid Id, string Role, string? DisplayName);
}
