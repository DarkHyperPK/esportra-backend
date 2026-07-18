using Dapper;
using Esportra.Contracts.Database;
using Esportra.Contracts.Responses;

namespace Esportra.Api.SponsorAnalytics;

public sealed class SponsorAnalyticsExportService(IDbConnectionFactory connectionFactory)
{
    private static readonly HashSet<string> AllowedReportTypes = new(StringComparer.Ordinal)
    {
        "summary", "performance", "placements", "content", "devices", "full",
    };

    public async Task<SponsorAnalyticsExportResponse?> RequestExportAsync(
        Guid sponsorId,
        Guid userId,
        string reportType,
        int days,
        CancellationToken cancellationToken)
    {
        if (!AllowedReportTypes.Contains(reportType)) return null;
        if (!SponsorAnalyticsPolicy.IsSupportedPeriod(days)) return null;

        using var connection = connectionFactory.CreateConnection();
        var row = await connection.QuerySingleAsync<ExportRow>(new CommandDefinition(
            """
            INSERT INTO public.sponsor_analytics_exports
                (sponsor_id, requested_by, report_type, period_days, status, requested_at)
            VALUES (@sponsorId, @userId, @reportType, @days, 'pending', NOW())
            RETURNING id AS Id, status AS Status, report_type AS ReportType,
                      period_days AS PeriodDays, requested_at AS RequestedAt,
                      completed_at AS CompletedAt, signed_url AS DownloadUrl,
                      url_expires_at AS UrlExpiresAt
            """,
            new { sponsorId, userId, reportType, days },
            cancellationToken: cancellationToken));

        return ToResponse(row);
    }

    public async Task<SponsorAnalyticsExportResponse?> GetExportStatusAsync(
        Guid exportId,
        Guid sponsorId,
        CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<ExportRow>(new CommandDefinition(
            """
            SELECT id AS Id, status AS Status, report_type AS ReportType,
                   period_days AS PeriodDays, requested_at AS RequestedAt,
                   completed_at AS CompletedAt, signed_url AS DownloadUrl,
                   url_expires_at AS UrlExpiresAt
            FROM public.sponsor_analytics_exports
            WHERE id = @exportId AND sponsor_id = @sponsorId
            """,
            new { exportId, sponsorId },
            cancellationToken: cancellationToken));

        return row is null ? null : ToResponse(row);
    }

    public async Task<IReadOnlyList<SponsorAnalyticsExportResponse>> ListExportsAsync(
        Guid sponsorId,
        CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<ExportRow>(new CommandDefinition(
            """
            SELECT id AS Id, status AS Status, report_type AS ReportType,
                   period_days AS PeriodDays, requested_at AS RequestedAt,
                   completed_at AS CompletedAt, signed_url AS DownloadUrl,
                   url_expires_at AS UrlExpiresAt
            FROM public.sponsor_analytics_exports
            WHERE sponsor_id = @sponsorId
            ORDER BY requested_at DESC
            LIMIT 20
            """,
            new { sponsorId },
            cancellationToken: cancellationToken));

        return rows.Select(ToResponse).ToList();
    }

    private static SponsorAnalyticsExportResponse ToResponse(ExportRow row) => new(
        row.Id,
        row.Status,
        row.ReportType,
        row.PeriodDays,
        row.RequestedAt,
        row.CompletedAt,
        row.UrlExpiresAt.HasValue && row.UrlExpiresAt.Value > DateTimeOffset.UtcNow ? row.DownloadUrl : null,
        row.UrlExpiresAt);

    private sealed record ExportRow(
        Guid Id,
        string Status,
        string ReportType,
        int PeriodDays,
        DateTimeOffset RequestedAt,
        DateTimeOffset? CompletedAt,
        string? DownloadUrl,
        DateTimeOffset? UrlExpiresAt);
}
