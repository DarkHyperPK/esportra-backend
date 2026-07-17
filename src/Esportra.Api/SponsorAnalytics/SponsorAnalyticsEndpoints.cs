using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Contracts.Requests;
using Dapper;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.SponsorAnalytics;

public static class SponsorAnalyticsEndpoints
{
    public static void MapSponsorAnalyticsEndpoints(this WebApplication app)
    {
        app.MapPost("/api/sponsor-analytics/events", RecordEventAsync)
            .AllowAnonymous()
            .WithMetadata(new RateLimitPolicyMetadata("sponsorAnalytics"));

        app.MapGet("/api/sponsors/me/audience", GetPartnerReportAsync)
            .RequireAuthorization("Authenticated");

        app.MapGet("/api/admin/sponsors/{sponsorId:guid}/audience", GetAdminReportAsync)
            .RequireAuthorization("Admin");
    }

    private static async Task<IResult> RecordEventAsync(
        [FromBody] RecordSponsorAnalyticsEventRequest request,
        HttpContext context,
        SponsorAnalyticsWriter writer,
        CancellationToken cancellationToken)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        var result = await writer.WriteAsync(request, userContext, context, cancellationToken);
        return result switch
        {
            SponsorAnalyticsWriteResult.Accepted => Results.Ok(new { accepted = true, duplicate = false }),
            SponsorAnalyticsWriteResult.Duplicate => Results.Ok(new { accepted = true, duplicate = true }),
            SponsorAnalyticsWriteResult.SponsorNotFound => Results.Ok(new { accepted = false }),
            _ => Results.BadRequest(new { error = "Invalid sponsor analytics event." }),
        };
    }

    private static async Task<IResult> GetPartnerReportAsync(
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        SponsorAudienceReportService reports,
        CancellationToken cancellationToken,
        int days = 30)
    {
        if (!SponsorAnalyticsPolicy.IsSupportedPeriod(days))
            return Results.BadRequest(new { error = "days must be 7, 30, or 90." });

        var userContext = context.Items["UserContext"] as UserContext;
        if (userContext is null) return Results.Unauthorized();

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
        if (sponsor is null) return Results.Forbid();
        if (!string.Equals(sponsor.Tier, "radiant", StringComparison.OrdinalIgnoreCase))
            return Results.Forbid();

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(await reports.GetAsync(sponsor.SponsorId, days, cancellationToken));
    }

    private static async Task<IResult> GetAdminReportAsync(
        Guid sponsorId,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        SponsorAudienceReportService reports,
        CancellationToken cancellationToken,
        int days = 30)
    {
        if (!SponsorAnalyticsPolicy.IsSupportedPeriod(days))
            return Results.BadRequest(new { error = "days must be 7, 30, or 90." });

        var userContext = context.Items["UserContext"] as UserContext;
        if (userContext is null) return Results.Unauthorized();
        if (!userContext.IsSuperAdmin) return Results.Forbid();

        using var connection = connectionFactory.CreateConnection();
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM public.sponsors WHERE id = @sponsorId)",
            new { sponsorId },
            cancellationToken: cancellationToken));
        if (!exists) return Results.NotFound();

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(await reports.GetAsync(sponsorId, days, cancellationToken));
    }

    private sealed record SponsorAccessRow
    {
        public Guid SponsorId { get; init; }
        public string Tier { get; init; } = string.Empty;
    }
}
