using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Licensing;
using Esportra.Core.Licensing;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>Event briefs, Pro forma invoices and private recovery. No money collection or license grants.</summary>
public static class ProEventRequestEndpoints
{
    public static void MapProEventRequestEndpoints(this WebApplication app)
    {
        app.MapPost("/api/pro-event-requests", CreateAsync).AllowAnonymous()
            .WithMetadata(new RateLimitPolicyMetadata("auth"), new RequestSizeLimitAttribute(8192));
        app.MapGet("/api/admin/pro-event-requests", ListAsync).RequireAuthorization("admin");
        app.MapPut("/api/admin/pro-event-requests/{id:guid}/status", UpdateAsync).RequireAuthorization("admin");
        app.MapProEventProofEndpoints();
        app.MapGet("/api/pro-event-requests/{id:guid}", ResumeAsync).AllowAnonymous()
            .WithMetadata(new RateLimitPolicyMetadata("strict"));
    }

    private static async Task<IResult> CreateAsync(ProEventRequest request, IDbConnectionFactory db, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5)).DateTime);
        var error = ProEventRequestRules.Validate(request, today);
        if (error is not null) return Results.BadRequest(new { error });
        var clean = ProEventRequestRules.Normalize(request);
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(clean)));
        using var conn = db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<SavedRequest>(new CommandDefinition(
            "SELECT reference AS Reference, estimated_amount AS EstimatedAmount, invoice_details::text AS InvoiceDetails, invoice_expires_at AS ExpiresAt, payload_hash AS PayloadHash FROM pro_event_requests WHERE id = @id",
            new { id = clean.RequestId }, cancellationToken: ct));
        if (existing is not null) return existing.PayloadHash == hash ? Results.Ok(ToReceipt(existing))
            : Results.Conflict(new { error = "This request identifier was already used for different details." });
        var amount = EventPassPricing.EstimatePkr(clean.TeamCount);
        var payment = amount is null ? null : await ProEventPaymentConfiguration.ReadAsync(db, ct);
        if (amount is not null && payment is null)
            return Results.Problem("Instant invoicing is not configured. Contact operations@esportra.com for this event.", statusCode: 503);
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clean.AccessToken)));
        var invoiceDetails = payment is null ? null : JsonSerializer.Serialize(payment);
        var expiresAt = amount is null ? (DateTime?)null : DateTime.UtcNow.AddDays(7);
        var saved = await conn.QuerySingleOrDefaultAsync<SavedRequest>(new CommandDefinition(
            """
            INSERT INTO public.pro_event_requests
                (id, reference, event_name, game_title, email, starts_on, ends_on, team_count, estimated_amount,
                 payload_hash, access_token_hash, invoice_details, invoice_expires_at)
            VALUES (@RequestId, @reference, @EventName, @GameTitle, @Email, @startsOn, @endsOn, @TeamCount,
                @amount, @hash, @tokenHash, @invoiceDetails::jsonb, @expiresAt)
            ON CONFLICT (id) DO UPDATE SET id = EXCLUDED.id
                WHERE pro_event_requests.payload_hash = EXCLUDED.payload_hash
            RETURNING reference AS Reference, estimated_amount AS EstimatedAmount,
                invoice_details::text AS InvoiceDetails, invoice_expires_at AS ExpiresAt, payload_hash AS PayloadHash
            """,
            new
            {
                clean.RequestId,
                reference = $"ESP-{clean.RequestId:N}".ToUpperInvariant(),
                clean.EventName,
                clean.GameTitle,
                clean.Email,
                startsOn = clean.StartsOn.ToDateTime(TimeOnly.MinValue),
                endsOn = clean.EndsOn.ToDateTime(TimeOnly.MinValue),
                clean.TeamCount,
                amount,
                hash,
                tokenHash,
                invoiceDetails,
                expiresAt
            }, cancellationToken: ct));
        if (saved is null) return Results.Conflict(new { error = "This request identifier was already used for different details." });
        return Results.Ok(ToReceipt(saved));
    }

    private static ProEventRequestReceipt ToReceipt(SavedRequest saved) => new(saved.Reference, saved.EstimatedAmount, "PKR",
        saved.InvoiceDetails is null ? null : new ProEventInvoice(saved.EstimatedAmount!.Value, "PKR", saved.ExpiresAt!.Value,
            JsonSerializer.Deserialize<ProEventPaymentDetails>(saved.InvoiceDetails)!));

    private static async Task<IResult> ResumeAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
    {
        var token = ctx.Request.Headers["X-Pro-Request-Token"].ToString();
        if (!ProEventProofEndpoints.ValidToken(token)) return Results.NotFound();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<ResumeRow>(new CommandDefinition(
            """
            SELECT reference AS Reference, estimated_amount AS EstimatedAmount, invoice_details::text AS InvoiceDetails,
              invoice_expires_at AS ExpiresAt, event_name AS EventName, game_title AS GameTitle, email AS Email,
              to_char(starts_on, 'YYYY-MM-DD') AS StartsOn, to_char(ends_on, 'YYYY-MM-DD') AS EndsOn,
              team_count AS TeamCount, status AS Status, (proof_image IS NOT NULL) AS HasProof
            FROM pro_event_requests WHERE id = @id AND access_token_hash = @hash
            """, new { id, hash }, cancellationToken: ct));
        ctx.Response.Headers.CacheControl = "no-store";
        if (row is null) return Results.NotFound();
        return Results.Ok(new
        {
            receipt = ToReceipt(new(row.Reference, row.EstimatedAmount, row.InvoiceDetails, row.ExpiresAt, "")),
            details = new { row.EventName, row.GameTitle, row.Email, row.StartsOn, row.EndsOn, row.TeamCount, website = "" },
            row.Status,
            proofSubmitted = row.HasProof
        });
    }

    private static async Task<IResult> ListAsync(HttpContext ctx, IDbConnectionFactory db, CancellationToken ct,
        int page = 0, string status = "pending")
    {
        if (!Can(ctx, Permissions.LicensesView)) return Results.Forbid();
        if (page is < 0 or > 10000 || !Statuses.Contains(status))
            return Results.BadRequest(new { error = "Invalid page or status." });
        using var conn = db.CreateConnection();
        var rows = (await conn.QueryAsync<ProEventRequestRow>(new CommandDefinition(
            """
            SELECT id AS Id, reference AS Reference, event_name AS EventName, game_title AS GameTitle,
                email AS Email, starts_on::timestamp AS StartsOn, ends_on::timestamp AS EndsOn, team_count AS TeamCount,
                estimated_amount AS EstimatedAmount, status AS Status, created_at AS CreatedAt,
                (proof_image IS NOT NULL) AS HasProof
            FROM public.pro_event_requests WHERE status = @status
            ORDER BY created_at, id LIMIT 26 OFFSET @offset
            """, new { status, offset = page * 25 }, cancellationToken: ct))).ToArray();
        return Results.Ok(new { items = rows.Take(25), hasMore = rows.Length > 25 });
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateProEventRequestStatus request,
        HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
    {
        if (!Can(ctx, Permissions.LicensesCreate)) return Results.Forbid();
        if (!Statuses.Contains(request.Status) || request.Status == "proof_submitted")
            return Results.BadRequest(new { error = "Invalid request status." });
        var user = (UserContext)ctx.Items["UserContext"]!;
        using var conn = db.CreateConnection();
        var count = await conn.ExecuteAsync(new CommandDefinition(
            """
            WITH changed AS (
                UPDATE public.pro_event_requests
                SET status = @status, handled_by = @actor, updated_at = now()
                WHERE id = @id RETURNING id
            )
            INSERT INTO public.pro_event_request_audit (request_id, actor_id, status)
            SELECT id, @actor, @status FROM changed
            """, new { id, status = request.Status, actor = user.UserIdGuid }, cancellationToken: ct));
        return count == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static bool Can(HttpContext ctx, string permission) => ctx.Items["UserContext"] is UserContext user
        && (user.IsSuperAdmin || user.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase));

    private static readonly string[] Statuses = ["pending", "contacted", "closed", "proof_submitted"];
    private sealed record SavedRequest(string Reference, int? EstimatedAmount, string? InvoiceDetails, DateTime? ExpiresAt, string PayloadHash);
    private sealed record ResumeRow(string Reference, int? EstimatedAmount, string? InvoiceDetails, DateTime? ExpiresAt,
        string EventName, string GameTitle, string Email, string StartsOn, string EndsOn, int TeamCount, string Status, bool HasProof);
    private sealed record ProEventRequestRow(Guid Id, string Reference, string EventName, string GameTitle,
        string Email, DateTime StartsOn, DateTime EndsOn, int TeamCount, int? EstimatedAmount, string Status, DateTime CreatedAt, bool HasProof);
}
