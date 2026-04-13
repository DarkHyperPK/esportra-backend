using Dapper;
using Esportra.Contracts.Auth;

namespace Esportra.Api.Endpoints;

public static class MemberEndpoints
{
    public static void MapMemberEndpoints(this WebApplication app)
    {
        // ── List members ────────────────────────────────────────
        app.MapGet("/api/venues/{id}/members", async (
            Guid id,
            string? q,
            string? tier,
            bool? banned,
            int page = 1,
            int pageSize = 25,
            IDbConnectionFactory db = null!,
            HttpContext ctx = null!,
            CancellationToken ct = default) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, id))
                return Results.Forbid();

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var offset = (page - 1) * pageSize;

            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT m.id, m.display_name, m.email, m.phone, m.avatar_url,
                       m.balance, m.loyalty_points, m.loyalty_tier,
                       m.total_hours, m.total_spent, m.total_sessions,
                       m.is_banned, m.ban_reason, m.notes, m.created_at
                FROM members m
                WHERE m.venue_id = @VenueId
                  AND (@Q IS NULL OR m.display_name ILIKE '%' || @Q || '%'
                       OR m.email ILIKE '%' || @Q || '%'
                       OR m.phone ILIKE '%' || @Q || '%')
                  AND (@Tier IS NULL OR m.loyalty_tier = @Tier)
                  AND (@Banned IS NULL OR m.is_banned = @Banned)
                ORDER BY m.created_at DESC
                LIMIT @Limit OFFSET @Offset
                """,
                new { VenueId = id, Q = q, Tier = tier, Banned = banned, Limit = pageSize, Offset = offset });

            var total = await conn.QuerySingleAsync<int>(
                """
                SELECT COUNT(*)
                FROM members
                WHERE venue_id = @VenueId
                  AND (@Q IS NULL OR display_name ILIKE '%' || @Q || '%'
                       OR email ILIKE '%' || @Q || '%'
                       OR phone ILIKE '%' || @Q || '%')
                  AND (@Tier IS NULL OR loyalty_tier = @Tier)
                  AND (@Banned IS NULL OR is_banned = @Banned)
                """,
                new { VenueId = id, Q = q, Tier = tier, Banned = banned });

            return Results.Ok(new { data = rows, total, page, page_size = pageSize });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Members");

        // ── Search members (fast autocomplete) ──────────────────
        app.MapGet("/api/venues/{id}/members/search", async (
            Guid id,
            string? q,
            int limit = 10,
            IDbConnectionFactory db = null!,
            HttpContext ctx = null!,
            CancellationToken ct = default) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, id))
                return Results.Forbid();

            limit = Math.Clamp(limit, 1, 50);

            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, display_name, email, phone, avatar_url,
                       balance, loyalty_tier, is_banned
                FROM members
                WHERE venue_id = @VenueId
                  AND is_banned = false
                  AND (@Q IS NULL OR display_name ILIKE '%' || @Q || '%'
                       OR email ILIKE '%' || @Q || '%'
                       OR phone ILIKE '%' || @Q || '%')
                ORDER BY display_name
                LIMIT @Limit
                """,
                new { VenueId = id, Q = q, Limit = limit });

            return Results.Ok(rows);
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Members");

        // ── Create member ───────────────────────────────────────
        app.MapPost("/api/venues/{id}/members", async (
            Guid id,
            CreateMemberRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, id))
                return Results.Forbid();

            if (string.IsNullOrWhiteSpace(req.DisplayName))
                return Results.BadRequest(new { error = "display_name is required" });

            using var conn = db.CreateConnection();

            // Check duplicate email within venue
            if (!string.IsNullOrWhiteSpace(req.Email))
            {
                var exists = await conn.QuerySingleAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM members WHERE venue_id = @VenueId AND LOWER(email) = LOWER(@Email))",
                    new { VenueId = id, req.Email });

                if (exists)
                    return Results.Conflict(new { error = "A member with this email already exists" });
            }

            var memberId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO members (venue_id, display_name, email, phone, notes, date_of_birth)
                VALUES (@VenueId, @DisplayName, @Email, @Phone, @Notes, @DateOfBirth)
                RETURNING id
                """,
                new
                {
                    VenueId = id,
                    req.DisplayName,
                    req.Email,
                    req.Phone,
                    req.Notes,
                    DateOfBirth = req.DateOfBirth,
                });

            return Results.Ok(new { id = memberId, display_name = req.DisplayName });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Members");

        // ── Get member detail ───────────────────────────────────
        app.MapGet("/api/venues/{id}/members/{memberId}", async (
            Guid id,
            Guid memberId,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, id))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var member = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM members WHERE id = @MemberId AND venue_id = @VenueId",
                new { MemberId = memberId, VenueId = id });

            if (member is null)
                return Results.NotFound(new { error = "Member not found" });

            // Recent sessions
            var sessions = await conn.QueryAsync<dynamic>(
                """
                SELECT id, station_id, session_type, started_at, ended_at,
                       total_charged, duration_minutes, zone
                FROM venue_sessions
                WHERE venue_id = @VenueId AND member_id = @MemberId
                ORDER BY started_at DESC
                LIMIT 20
                """,
                new { VenueId = id, MemberId = memberId });

            // Recent invoices
            var invoices = await conn.QueryAsync<dynamic>(
                """
                SELECT id, total, payment_method, status, paid_at, created_at
                FROM session_invoices
                WHERE venue_id = @VenueId AND member_id = @MemberId
                ORDER BY created_at DESC
                LIMIT 20
                """,
                new { VenueId = id, MemberId = memberId });

            return Results.Ok(new { member, recent_sessions = sessions, recent_invoices = invoices });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Members");

        // ── Update member ───────────────────────────────────────
        app.MapPut("/api/venues/{id}/members/{memberId}", async (
            Guid id,
            Guid memberId,
            UpdateMemberRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, id))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var affected = await conn.ExecuteAsync(
                """
                UPDATE members
                SET display_name  = COALESCE(@DisplayName, display_name),
                    email         = COALESCE(@Email, email),
                    phone         = COALESCE(@Phone, phone),
                    notes         = COALESCE(@Notes, notes),
                    date_of_birth = COALESCE(@DateOfBirth, date_of_birth),
                    updated_at    = NOW()
                WHERE id = @MemberId AND venue_id = @VenueId
                """,
                new
                {
                    MemberId = memberId,
                    VenueId = id,
                    req.DisplayName,
                    req.Email,
                    req.Phone,
                    req.Notes,
                    req.DateOfBirth,
                });

            return affected > 0
                ? Results.Ok(new { updated = true })
                : Results.NotFound(new { error = "Member not found" });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Members");

        // ── Top up member balance ───────────────────────────────
        app.MapPost("/api/venues/{id}/members/{memberId}/topup", async (
            Guid id,
            Guid memberId,
            TopupRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, id))
                return Results.Forbid();

            if (req.Amount <= 0)
                return Results.BadRequest(new { error = "Amount must be positive" });

            using var conn = db.CreateConnection();

            var newBalance = await conn.QuerySingleOrDefaultAsync<decimal?>(
                """
                UPDATE members
                SET balance = balance + @Amount, updated_at = NOW()
                WHERE id = @MemberId AND venue_id = @VenueId
                RETURNING balance
                """,
                new { MemberId = memberId, VenueId = id, req.Amount });

            if (newBalance is null)
                return Results.NotFound(new { error = "Member not found" });

            return Results.Ok(new { balance = newBalance, amount_added = req.Amount });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Members");

        // ── Ban member ──────────────────────────────────────────
        app.MapPost("/api/venues/{id}/members/{memberId}/ban", async (
            Guid id,
            Guid memberId,
            BanRequest? req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, id))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var affected = await conn.ExecuteAsync(
                """
                UPDATE members
                SET is_banned = true, ban_reason = @Reason, banned_at = NOW(), updated_at = NOW()
                WHERE id = @MemberId AND venue_id = @VenueId
                """,
                new { MemberId = memberId, VenueId = id, Reason = req?.Reason });

            return affected > 0
                ? Results.Ok(new { banned = true })
                : Results.NotFound(new { error = "Member not found" });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Members");

        // ── Unban member ────────────────────────────────────────
        app.MapPost("/api/venues/{id}/members/{memberId}/unban", async (
            Guid id,
            Guid memberId,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, id))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var affected = await conn.ExecuteAsync(
                """
                UPDATE members
                SET is_banned = false, ban_reason = NULL, banned_at = NULL, updated_at = NOW()
                WHERE id = @MemberId AND venue_id = @VenueId
                """,
                new { MemberId = memberId, VenueId = id });

            return affected > 0
                ? Results.Ok(new { unbanned = true })
                : Results.NotFound(new { error = "Member not found" });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Members");
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

// ── Request DTOs ────────────────────────────────────────────────
public record CreateMemberRequest(
    string DisplayName,
    string? Email = null,
    string? Phone = null,
    string? Notes = null,
    DateTime? DateOfBirth = null);

public record UpdateMemberRequest(
    string? DisplayName = null,
    string? Email = null,
    string? Phone = null,
    string? Notes = null,
    DateTime? DateOfBirth = null);

public record TopupRequest(decimal Amount, string? Description = null);
public record BanRequest(string? Reason = null);
