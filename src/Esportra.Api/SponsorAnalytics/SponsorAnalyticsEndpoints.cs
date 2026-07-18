using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Contracts.Requests;
using Dapper;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.SponsorAnalytics;

public static class SponsorAnalyticsEndpoints
{
    private static readonly Dictionary<string, int> TierOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["partner"] = 1,
        ["standard"] = 1,
        ["diamond"] = 1,
        ["ascendant"] = 2,
        ["radiant"] = 3,
    };

    public static void MapSponsorAnalyticsEndpoints(this WebApplication app)
    {
        // Event ingestion (anonymous, rate-limited)
        app.MapPost("/api/sponsor-analytics/events", RecordEventAsync)
            .AllowAnonymous()
            .WithMetadata(new RateLimitPolicyMetadata("sponsorAnalytics"));

        // Audience demographics (existing)
        app.MapGet("/api/sponsors/me/audience", GetPartnerAudienceAsync)
            .RequireAuthorization("Authenticated");

        // Partner analytics endpoints
        app.MapGet("/api/sponsors/me/analytics/summary", GetPartnerSummaryAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/sponsors/me/analytics/performance", GetPartnerPerformanceAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/sponsors/me/analytics/placements", GetPartnerPlacementsAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/sponsors/me/analytics/content", GetPartnerContentAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/sponsors/me/analytics/devices", GetPartnerDevicesAsync)
            .RequireAuthorization("Authenticated");

        // Partner export endpoints
        app.MapPost("/api/sponsors/me/analytics/export", RequestPartnerExportAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/sponsors/me/analytics/exports", ListPartnerExportsAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/sponsors/me/analytics/exports/{exportId:guid}", GetPartnerExportStatusAsync)
            .RequireAuthorization("Authenticated");

        // Admin analytics endpoints
        app.MapGet("/api/admin/sponsors/{sponsorId:guid}/audience", GetAdminAudienceAsync)
            .RequireAuthorization("Admin");
        app.MapGet("/api/admin/sponsors/{sponsorId:guid}/analytics/summary", GetAdminSummaryAsync)
            .RequireAuthorization("Admin");
        app.MapGet("/api/admin/sponsors/{sponsorId:guid}/analytics/performance", GetAdminPerformanceAsync)
            .RequireAuthorization("Admin");
        app.MapGet("/api/admin/sponsors/{sponsorId:guid}/analytics/placements", GetAdminPlacementsAsync)
            .RequireAuthorization("Admin");
        app.MapGet("/api/admin/sponsors/{sponsorId:guid}/analytics/content", GetAdminContentAsync)
            .RequireAuthorization("Admin");
        app.MapGet("/api/admin/sponsors/{sponsorId:guid}/analytics/devices", GetAdminDevicesAsync)
            .RequireAuthorization("Admin");
    }

    // ─── Event Ingestion ─────────────────────────────────────────────────

    private static async Task<IResult> RecordEventAsync(
        [FromBody] RecordSponsorAnalyticsEventRequest request,
        HttpContext context,
        SponsorAnalyticsWriter writer,
        CancellationToken cancellationToken)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        var result = await writer.WriteAsync(request, userContext, context, cancellationToken);
        return result.Result switch
        {
            SponsorAnalyticsWriteResult.Accepted => Results.Ok(new { accepted = true, duplicate = false }),
            SponsorAnalyticsWriteResult.Duplicate => Results.Ok(new { accepted = true, duplicate = true }),
            SponsorAnalyticsWriteResult.SponsorNotFound => Results.Ok(new { accepted = false }),
            _ => Results.BadRequest(new { error = "Invalid sponsor analytics event.", code = result.Reason }),
        };
    }

    // ─── Partner Endpoints ───────────────────────────────────────────────

    private static async Task<IResult> GetPartnerAudienceAsync(
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        SponsorAudienceReportService reports,
        CancellationToken cancellationToken,
        int days = 30)
    {
        var access = await ResolvePartnerAccessAsync(context, connectionFactory, "radiant", cancellationToken);
        if (access.Error is not null) return access.Error;
        if (!SponsorAnalyticsPolicy.IsSupportedPeriod(days))
            return Results.BadRequest(new { error = "days must be 7, 30, or 90." });

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(await reports.GetAsync(access.SponsorId, days, cancellationToken));
    }

    private static async Task<IResult> GetPartnerSummaryAsync(
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        SponsorPerformanceReportService reports,
        CancellationToken cancellationToken,
        int days = 30)
    {
        var access = await ResolvePartnerAccessAsync(context, connectionFactory, "partner", cancellationToken);
        if (access.Error is not null) return access.Error;
        if (!SponsorAnalyticsPolicy.IsSupportedPeriod(days))
            return Results.BadRequest(new { error = "days must be 7, 30, or 90." });

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(await reports.GetSummaryAsync(access.SponsorId, days, cancellationToken));
    }

    private static async Task<IResult> GetPartnerPerformanceAsync(
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        SponsorPerformanceReportService reports,
        CancellationToken cancellationToken,
        int days = 30)
    {
        var access = await ResolvePartnerAccessAsync(context, connectionFactory, "partner", cancellationToken);
        if (access.Error is not null) return access.Error;
        if (!SponsorAnalyticsPolicy.IsSupportedPeriod(days))
            return Results.BadRequest(new { error = "days must be 7, 30, or 90." });

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(await reports.GetPerformanceAsync(access.SponsorId, days, cancellationToken));
    }

    private static async Task<IResult> GetPartnerPlacementsAsync(
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        SponsorPerformanceReportService reports,
        CancellationToken cancellationToken,
        int days = 30)
    {
        var access = await ResolvePartnerAccessAsync(context, connectionFactory, "ascendant", cancellationToken);
        if (access.Error is not null) return access.Error;
        if (!SponsorAnalyticsPolicy.IsSupportedPeriod(days))
            return Results.BadRequest(new { error = "days must be 7, 30, or 90." });

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(await reports.GetPlacementsAsync(access.SponsorId, days, cancellationToken));
    }

    private static async Task<IResult> GetPartnerContentAsync(
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        SponsorPerformanceReportService reports,
        CancellationToken cancellationToken,
        int days = 30)
    {
        var access = await ResolvePartnerAccessAsync(context, connectionFactory, "radiant", cancellationToken);
        if (access.Error is not null) return access.Error;
        if (!SponsorAnalyticsPolicy.IsSupportedPeriod(days))
            return Results.BadRequest(new { error = "days must be 7, 30, or 90." });

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(await reports.GetContentAsync(access.SponsorId, days, cancellationToken));
    }

    private static async Task<IResult> GetPartnerDevicesAsync(
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        SponsorPerformanceReportService reports,
        CancellationToken cancellationToken,
        int days = 30)
    {
        var access = await ResolvePartnerAccessAsync(context, connectionFactory, "radiant", cancellationToken);
        if (access.Error is not null) return access.Error;
        if (!SponsorAnalyticsPolicy.IsSupportedPeriod(days))
            return Results.BadRequest(new { error = "days must be 7, 30, or 90." });

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(await reports.GetDevicesAsync(access.SponsorId, days, cancellationToken));
    }

    // ─── Partner Export Endpoints ────────────────────────────────────────

    private static async Task<IResult> RequestPartnerExportAsync(
        [FromBody] RequestSponsorAnalyticsExportRequest request,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        SponsorAnalyticsExportService exports,
        CancellationToken cancellationToken)
    {
        var access = await ResolvePartnerAccessAsync(context, connectionFactory, "partner", cancellationToken);
        if (access.Error is not null) return access.Error;

        var result = await exports.RequestExportAsync(
            access.SponsorId, access.UserId, request.ReportType, request.Days, cancellationToken);
        if (result is null)
            return Results.BadRequest(new { error = "Invalid report type or period." });

        return Results.Accepted(value: result);
    }

    private static async Task<IResult> ListPartnerExportsAsync(
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        SponsorAnalyticsExportService exports,
        CancellationToken cancellationToken)
    {
        var access = await ResolvePartnerAccessAsync(context, connectionFactory, "partner", cancellationToken);
        if (access.Error is not null) return access.Error;

        return Results.Ok(await exports.ListExportsAsync(access.SponsorId, cancellationToken));
    }

    private static async Task<IResult> GetPartnerExportStatusAsync(
        Guid exportId,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        SponsorAnalyticsExportService exports,
        CancellationToken cancellationToken)
    {
        var access = await ResolvePartnerAccessAsync(context, connectionFactory, "partner", cancellationToken);
        if (access.Error is not null) return access.Error;

        var result = await exports.GetExportStatusAsync(exportId, access.SponsorId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    // ─── Admin Endpoints ─────────────────────────────────────────────────

    private static async Task<IResult> GetAdminAudienceAsync(
        Guid sponsorId,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        SponsorAudienceReportService reports,
        CancellationToken cancellationToken,
        int days = 30)
    {
        var error = ValidateAdminAccess(context);
        if (error is not null) return error;
        if (!SponsorAnalyticsPolicy.IsSupportedPeriod(days))
            return Results.BadRequest(new { error = "days must be 7, 30, or 90." });
        if (!await SponsorExistsAsync(connectionFactory, sponsorId, cancellationToken))
            return Results.NotFound();

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(await reports.GetAsync(sponsorId, days, cancellationToken));
    }

    private static async Task<IResult> GetAdminSummaryAsync(
        Guid sponsorId,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        SponsorPerformanceReportService reports,
        CancellationToken cancellationToken,
        int days = 30)
    {
        var error = ValidateAdminAccess(context);
        if (error is not null) return error;
        if (!SponsorAnalyticsPolicy.IsSupportedPeriod(days))
            return Results.BadRequest(new { error = "days must be 7, 30, or 90." });
        if (!await SponsorExistsAsync(connectionFactory, sponsorId, cancellationToken))
            return Results.NotFound();

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(await reports.GetSummaryAsync(sponsorId, days, cancellationToken));
    }

    private static async Task<IResult> GetAdminPerformanceAsync(
        Guid sponsorId,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        SponsorPerformanceReportService reports,
        CancellationToken cancellationToken,
        int days = 30)
    {
        var error = ValidateAdminAccess(context);
        if (error is not null) return error;
        if (!SponsorAnalyticsPolicy.IsSupportedPeriod(days))
            return Results.BadRequest(new { error = "days must be 7, 30, or 90." });
        if (!await SponsorExistsAsync(connectionFactory, sponsorId, cancellationToken))
            return Results.NotFound();

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(await reports.GetPerformanceAsync(sponsorId, days, cancellationToken));
    }

    private static async Task<IResult> GetAdminPlacementsAsync(
        Guid sponsorId,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        SponsorPerformanceReportService reports,
        CancellationToken cancellationToken,
        int days = 30)
    {
        var error = ValidateAdminAccess(context);
        if (error is not null) return error;
        if (!SponsorAnalyticsPolicy.IsSupportedPeriod(days))
            return Results.BadRequest(new { error = "days must be 7, 30, or 90." });
        if (!await SponsorExistsAsync(connectionFactory, sponsorId, cancellationToken))
            return Results.NotFound();

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(await reports.GetPlacementsAsync(sponsorId, days, cancellationToken));
    }

    private static async Task<IResult> GetAdminContentAsync(
        Guid sponsorId,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        SponsorPerformanceReportService reports,
        CancellationToken cancellationToken,
        int days = 30)
    {
        var error = ValidateAdminAccess(context);
        if (error is not null) return error;
        if (!SponsorAnalyticsPolicy.IsSupportedPeriod(days))
            return Results.BadRequest(new { error = "days must be 7, 30, or 90." });
        if (!await SponsorExistsAsync(connectionFactory, sponsorId, cancellationToken))
            return Results.NotFound();

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(await reports.GetContentAsync(sponsorId, days, cancellationToken));
    }

    private static async Task<IResult> GetAdminDevicesAsync(
        Guid sponsorId,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        SponsorPerformanceReportService reports,
        CancellationToken cancellationToken,
        int days = 30)
    {
        var error = ValidateAdminAccess(context);
        if (error is not null) return error;
        if (!SponsorAnalyticsPolicy.IsSupportedPeriod(days))
            return Results.BadRequest(new { error = "days must be 7, 30, or 90." });
        if (!await SponsorExistsAsync(connectionFactory, sponsorId, cancellationToken))
            return Results.NotFound();

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(await reports.GetDevicesAsync(sponsorId, days, cancellationToken));
    }

    // ─── Shared Helpers ──────────────────────────────────────────────────

    private static async Task<PartnerAccessResult> ResolvePartnerAccessAsync(
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        string requiredTier,
        CancellationToken cancellationToken)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (userContext is null)
            return new PartnerAccessResult(Guid.Empty, Guid.Empty, Results.Unauthorized());

        using var connection = connectionFactory.CreateConnection();
        var sponsor = await connection.QuerySingleOrDefaultAsync<SponsorAccessRow>(new CommandDefinition(
            """
            SELECT sponsor.id AS SponsorId, sponsor.tier AS Tier
            FROM public.sponsor_accounts AS account
            JOIN public.sponsors AS sponsor ON sponsor.id = account.sponsor_id
            WHERE account.user_id = @userId AND account.status = 'active'
            LIMIT 1
            """,
            new { userId = userContext.UserIdGuid },
            cancellationToken: cancellationToken));

        if (sponsor is null)
            return new PartnerAccessResult(Guid.Empty, Guid.Empty, Results.Forbid());
        if (!TierAtLeast(sponsor.Tier, requiredTier))
            return new PartnerAccessResult(Guid.Empty, Guid.Empty, Results.Forbid());

        return new PartnerAccessResult(sponsor.SponsorId, userContext.UserIdGuid, null);
    }

    private static IResult? ValidateAdminAccess(HttpContext context)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (userContext is null) return Results.Unauthorized();
        if (!userContext.IsSuperAdmin) return Results.Forbid();
        return null;
    }

    private static async Task<bool> SponsorExistsAsync(
        IDbConnectionFactory connectionFactory,
        Guid sponsorId,
        CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM public.sponsors WHERE id = @sponsorId)",
            new { sponsorId },
            cancellationToken: cancellationToken));
    }

    internal static bool TierAtLeast(string actualTier, string requiredTier) =>
        TierOrder.GetValueOrDefault(actualTier, 1) >= TierOrder.GetValueOrDefault(requiredTier, 1);

    private readonly record struct PartnerAccessResult(Guid SponsorId, Guid UserId, IResult? Error);

    private sealed record SponsorAccessRow
    {
        public Guid SponsorId { get; init; }
        public string Tier { get; init; } = string.Empty;
    }
}
