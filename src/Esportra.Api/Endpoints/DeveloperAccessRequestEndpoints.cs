using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Developer API access request endpoints.
/// Organizers submit live API access applications; admins review them.
/// </summary>
public static class DeveloperAccessRequestEndpoints
{
    public static void MapDeveloperAccessRequestEndpoints(this WebApplication app)
    {
        app.MapPost("/api/developer/access-requests", SubmitAccessRequest)
            .RequireAuthorization("Authenticated").WithTags("Developer Access Requests");

        app.MapGet("/api/developer/access-requests", GetAccessRequest)
            .RequireAuthorization("Authenticated").WithTags("Developer Access Requests");

        app.MapGet("/api/admin/developer-access-requests", AdminListAccessRequests)
            .RequireAuthorization(Permissions.DeveloperKeysManage).WithTags("Developer Admin");

        app.MapPatch("/api/admin/developer-access-requests/{requestId}", AdminReviewAccessRequest)
            .RequireAuthorization(Permissions.DeveloperKeysManage).WithTags("Developer Admin");
    }

    private static async Task<IResult> SubmitAccessRequest(
        [FromBody] SubmitAccessRequestRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(req.OrganizationId)) return Results.BadRequest(new { error = "organization_id is required" });
        if (!Guid.TryParse(req.OrganizationId, out var orgId)) return Results.BadRequest(new { error = "invalid organization_id" });

        var validationError = ValidateIntendedUse(req.IntendedUse);
        if (validationError is not null) return Results.BadRequest(new { error = validationError });

        using var conn = db.CreateConnection();
        if (!await IsOrgOwnerOrAdmin(conn, orgId, userCtx.UserIdGuid)) return Results.Forbid();

        var checks = await LoadOrgAccessChecks(conn, orgId);
        if (checks.IsApiApproved)
            return Results.UnprocessableEntity(new { error = "Organization already has API access" });
        if (checks.HasPendingRequest)
            return Results.Conflict(new { error = "A pending application already exists" });
        if (checks.HasRecentSubmission)
            return Results.StatusCode(429);

        var id = Guid.NewGuid();
        await conn.ExecuteAsync(
            """
            INSERT INTO developer_access_requests (id, organization_id, requested_by, intended_use, status)
            VALUES (@id, @orgId, @requestedBy, @intendedUse, 'pending')
            """,
            new { id, orgId, requestedBy = userCtx.UserIdGuid, intendedUse = req.IntendedUse });

        return Results.Created(
            $"/api/developer/access-requests",
            new { id, status = "pending", created_at = DateTimeOffset.UtcNow });
    }

    private static async Task<IResult> GetAccessRequest(
        [FromQuery] string organizationId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (!Guid.TryParse(organizationId, out var orgId)) return Results.BadRequest(new { error = "invalid organization_id" });

        using var conn = db.CreateConnection();
        if (!await IsOrgOwnerOrAdmin(conn, orgId, userCtx.UserIdGuid)) return Results.Forbid();

        var row = await conn.QuerySingleOrDefaultAsync<AccessRequestRow>(
            """
            SELECT id, status, intended_use AS IntendedUse, created_at AS CreatedAt,
                   reviewed_at AS ReviewedAt, admin_notes AS AdminNotes
            FROM developer_access_requests
            WHERE organization_id = @orgId
            ORDER BY created_at DESC
            LIMIT 1
            """,
            new { orgId });

        if (row is null) return Results.NotFound();

        return Results.Ok(new
        {
            row.Id,
            row.Status,
            intended_use = row.IntendedUse,
            created_at = row.CreatedAt,
            reviewed_at = row.ReviewedAt,
            admin_notes = row.AdminNotes,
        });
    }

    private static async Task<IResult> AdminListAccessRequests(
        [FromQuery] string? status,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var offset = (page - 1) * pageSize;
        var statusFilter = NormalizeStatus(status);

        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<AdminAccessRequestRow>(
            """
            SELECT r.id, r.organization_id, o.name AS organization_name,
                   r.status, r.intended_use, r.admin_notes,
                   r.created_at AS submitted_at, r.reviewed_at,
                   u.email AS requester_email,
                   COALESCE(u.raw_user_meta_data->>'full_name', u.email) AS requester_name
            FROM developer_access_requests r
            LEFT JOIN organizations o ON o.id = r.organization_id
            LEFT JOIN auth.users u ON u.id = r.requested_by
            WHERE (@status IS NULL OR r.status = @status)
            ORDER BY r.created_at DESC
            LIMIT @pageSize OFFSET @offset
            """,
            new { status = statusFilter, pageSize, offset });

        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM developer_access_requests WHERE (@status IS NULL OR status = @status)",
            new { status = statusFilter });

        var items = rows.Select(r => new
        {
            r.Id,
            organization_id = r.OrganizationId,
            organization_name = r.OrganizationName,
            r.Status,
            intended_use = r.IntendedUse,
            admin_notes = r.AdminNotes,
            submitted_at = r.SubmittedAt,
            reviewed_at = r.ReviewedAt,
            requester_email = r.RequesterEmail,
            requester_name = r.RequesterName,
        });

        return Results.Ok(new { items, total, page, pageSize });
    }

    private static async Task<IResult> AdminReviewAccessRequest(
        Guid requestId,
        [FromBody] ReviewAccessRequestRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        var action = req.Action?.ToLowerInvariant();
        if (action is not ("approve" or "reject"))
            return Results.BadRequest(new { error = "action must be 'approve' or 'reject'" });
        if (req.AdminNotes is { Length: > 5000 })
            return Results.BadRequest(new { error = "admin_notes must not exceed 5000 characters" });

        using var conn = db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<(string Status, Guid OrgId)>(
            "SELECT status AS Status, organization_id AS OrgId FROM developer_access_requests WHERE id = @requestId",
            new { requestId });

        if (existing == default) return Results.NotFound();
        if (existing.Status != "pending")
            return Results.Conflict(new { error = "Request has already been processed" });

        if (action == "approve")
            await ApproveRequestAsync(conn, requestId, existing.OrgId, userCtx.UserIdGuid, req.AdminNotes);
        else
            await RejectRequestAsync(conn, requestId, userCtx.UserIdGuid, req.AdminNotes);

        var updated = await conn.QuerySingleOrDefaultAsync<(Guid Id, string Status, Guid OrgId, DateTimeOffset ReviewedAt)>(
            "SELECT id AS Id, status AS Status, organization_id AS OrgId, reviewed_at AS ReviewedAt FROM developer_access_requests WHERE id = @requestId",
            new { requestId });

        return Results.Ok(new
        {
            id = updated.Id,
            status = updated.Status,
            organization_id = updated.OrgId,
            reviewed_at = updated.ReviewedAt,
        });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static async Task<bool> IsOrgOwnerOrAdmin(System.Data.IDbConnection conn, Guid orgId, Guid userId)
    {
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM organizations WHERE id = @orgId AND owner_id = @userId
                UNION ALL
                SELECT 1 FROM organization_staff WHERE organization_id = @orgId AND user_id = @userId AND role = 'admin' AND status = 'active'
            )
            """,
            new { orgId, userId });
    }

    private static async Task<OrgAccessChecks> LoadOrgAccessChecks(System.Data.IDbConnection conn, Guid orgId)
    {
        var isApproved = await conn.ExecuteScalarAsync<bool>(
            "SELECT is_api_approved FROM organizations WHERE id = @orgId",
            new { orgId });

        var hasPending = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM developer_access_requests WHERE organization_id = @orgId AND status = 'pending')",
            new { orgId });

        var hasRecent = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM developer_access_requests WHERE organization_id = @orgId AND created_at > NOW() - INTERVAL '24 hours')",
            new { orgId });

        return new OrgAccessChecks(isApproved, hasPending, hasRecent);
    }

    private static async Task ApproveRequestAsync(
        System.Data.IDbConnection conn,
        Guid requestId,
        Guid orgId,
        Guid reviewedBy,
        string? adminNotes)
    {
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(
            "UPDATE organizations SET is_api_approved = true WHERE id = @orgId",
            new { orgId }, tx);

        await conn.ExecuteAsync(
            """
            UPDATE developer_access_requests
            SET status = 'approved', reviewed_by = @reviewedBy, reviewed_at = NOW(), admin_notes = @adminNotes
            WHERE id = @requestId
            """,
            new { requestId, reviewedBy, adminNotes }, tx);
        tx.Commit();
    }

    private static async Task RejectRequestAsync(
        System.Data.IDbConnection conn,
        Guid requestId,
        Guid reviewedBy,
        string? adminNotes)
    {
        await conn.ExecuteAsync(
            """
            UPDATE developer_access_requests
            SET status = 'rejected', reviewed_by = @reviewedBy, reviewed_at = NOW(), admin_notes = @adminNotes
            WHERE id = @requestId
            """,
            new { requestId, reviewedBy, adminNotes });
    }

    internal static string? ValidateIntendedUse(string? intendedUse)
    {
        if (string.IsNullOrWhiteSpace(intendedUse)) return "intended_use is required";
        if (intendedUse.Length < 50) return "intended_use must be at least 50 characters";
        if (intendedUse.Length > 2000) return "intended_use must not exceed 2000 characters";
        return null;
    }

    internal static string? NormalizeStatus(string? status) =>
        status?.ToLowerInvariant() switch
        {
            "pending" or "approved" or "rejected" => status.ToLowerInvariant(),
            _ => null,
        };

    private sealed record OrgAccessChecks(bool IsApiApproved, bool HasPendingRequest, bool HasRecentSubmission);

    private sealed record AdminAccessRequestRow
    {
        public Guid Id { get; init; }
        public Guid OrganizationId { get; init; }
        public string? OrganizationName { get; init; }
        public string Status { get; init; } = string.Empty;
        public string IntendedUse { get; init; } = string.Empty;
        public string? AdminNotes { get; init; }
        public DateTimeOffset SubmittedAt { get; init; }
        public DateTimeOffset? ReviewedAt { get; init; }
        public string? RequesterEmail { get; init; }
        public string? RequesterName { get; init; }
    }

    private sealed record AccessRequestRow
    {
        public Guid Id { get; init; }
        public string Status { get; init; } = string.Empty;
        public string IntendedUse { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? ReviewedAt { get; init; }
        public string? AdminNotes { get; init; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Request records
// ─────────────────────────────────────────────────────────────────────────────

public sealed record SubmitAccessRequestRequest(
    string OrganizationId,
    string? IntendedUse = null);

public sealed record ReviewAccessRequestRequest(
    string? Action = null,
    string? AdminNotes = null);
