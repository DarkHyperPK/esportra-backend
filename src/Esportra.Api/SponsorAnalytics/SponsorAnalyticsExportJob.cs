using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Dapper;
using Esportra.Contracts.Database;
using Esportra.Contracts.Responses;

namespace Esportra.Api.SponsorAnalytics;

public sealed class SponsorAnalyticsExportJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<SponsorAnalyticsExportJob> logger) : BackgroundService
{
    private const string BucketName = "sponsor-exports";
    private const int UrlExpirySeconds = 86400; // 24 hours
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingExportsAsync(stoppingToken);
                await ExpireOldExportsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Export job cycle failed");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task ProcessPendingExportsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        var reportService = scope.ServiceProvider.GetRequiredService<SponsorPerformanceReportService>();

        using var connection = connectionFactory.CreateConnection();

        var pending = await connection.QueryAsync<PendingExportRow>(new CommandDefinition(
            """
            UPDATE public.sponsor_analytics_exports
            SET status = 'processing'
            WHERE id IN (
                SELECT id FROM public.sponsor_analytics_exports
                WHERE status = 'pending'
                ORDER BY requested_at ASC
                LIMIT 5
            )
            RETURNING id AS Id, sponsor_id AS SponsorId, report_type AS ReportType, period_days AS PeriodDays
            """,
            cancellationToken: ct));

        foreach (var export in pending)
        {
            try
            {
                var csv = await GenerateCsvAsync(reportService, export, ct);
                var storagePath = $"{export.SponsorId}/{export.Id}.csv";
                await UploadToStorageAsync(csv, storagePath, ct);
                var signedUrl = await CreateSignedUrlAsync(storagePath, ct);

                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE public.sponsor_analytics_exports
                    SET status = 'completed', file_path = @filePath, signed_url = @signedUrl,
                        url_expires_at = @urlExpiresAt, completed_at = NOW()
                    WHERE id = @id
                    """,
                    new
                    {
                        id = export.Id,
                        filePath = storagePath,
                        signedUrl,
                        urlExpiresAt = DateTimeOffset.UtcNow.AddSeconds(UrlExpirySeconds),
                    },
                    cancellationToken: ct));

                logger.LogInformation("Export {ExportId} completed for sponsor {SponsorId}", export.Id, export.SponsorId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Export {ExportId} failed", export.Id);
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE public.sponsor_analytics_exports
                    SET status = 'failed', error_message = @errorMessage, completed_at = NOW()
                    WHERE id = @id
                    """,
                    new { id = export.Id, errorMessage = ex.Message[..Math.Min(ex.Message.Length, 500)] },
                    cancellationToken: ct));
            }
        }
    }

    private static async Task<string> GenerateCsvAsync(
        SponsorPerformanceReportService reportService,
        PendingExportRow export,
        CancellationToken ct)
    {
        var sb = new StringBuilder();

        if (export.ReportType is "performance" or "full")
        {
            var perf = await reportService.GetPerformanceAsync(export.SponsorId, export.PeriodDays, ct);
            sb.AppendLine("--- Performance ---");
            sb.AppendLine("Date,Impressions,Clicks,CTR");
            foreach (var day in perf.Days)
                sb.AppendLine(CultureInfo.InvariantCulture, $"{day.Date:yyyy-MM-dd},{day.Impressions},{day.Clicks},{day.Ctr}");
            sb.AppendLine();
        }

        if (export.ReportType is "placements" or "full")
        {
            var placements = await reportService.GetPlacementsAsync(export.SponsorId, export.PeriodDays, ct);
            sb.AppendLine("--- Placements ---");
            sb.AppendLine("Placement,Impressions,Clicks,CTR");
            foreach (var p in placements.Placements)
                sb.AppendLine(CultureInfo.InvariantCulture, $"{p.Placement},{p.Impressions},{p.Clicks},{p.Ctr}");
            sb.AppendLine();
        }

        if (export.ReportType is "content" or "full")
        {
            var content = await reportService.GetContentAsync(export.SponsorId, export.PeriodDays, ct);
            sb.AppendLine("--- Tournaments ---");
            sb.AppendLine("TournamentId,TournamentName,Impressions,Clicks,CTR");
            foreach (var t in content.Tournaments)
                sb.AppendLine(CultureInfo.InvariantCulture, $"{t.TournamentId},{EscapeCsv(t.TournamentName)},{t.Impressions},{t.Clicks},{t.Ctr}");
            sb.AppendLine();
            sb.AppendLine("--- Pages ---");
            sb.AppendLine("PagePath,Impressions,Clicks,CTR");
            foreach (var p in content.Pages)
                sb.AppendLine(CultureInfo.InvariantCulture, $"{EscapeCsv(p.PagePath)},{p.Impressions},{p.Clicks},{p.Ctr}");
            sb.AppendLine();
        }

        if (export.ReportType is "devices" or "full")
        {
            var devices = await reportService.GetDevicesAsync(export.SponsorId, export.PeriodDays, ct);
            sb.AppendLine("--- Devices ---");
            sb.AppendLine("DeviceClass,Impressions,Clicks,CTR");
            foreach (var d in devices.Devices)
                sb.AppendLine(CultureInfo.InvariantCulture, $"{d.DeviceClass},{d.Impressions},{d.Clicks},{d.Ctr}");
            sb.AppendLine();
        }

        if (export.ReportType is "summary" or "full")
        {
            var summary = await reportService.GetSummaryAsync(export.SponsorId, export.PeriodDays, ct);
            sb.AppendLine("--- Summary ---");
            sb.AppendLine("Metric,Value");
            sb.AppendLine(CultureInfo.InvariantCulture, $"TotalImpressions,{summary.TotalImpressions}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"TotalClicks,{summary.TotalClicks}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"CTR,{summary.Ctr}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"UniqueAudience,{summary.UniqueAudience}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"ImpressionsChange%,{summary.Trend.ImpressionsChangePercent}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"ClicksChange%,{summary.Trend.ClicksChangePercent}");
        }

        return sb.ToString();
    }

    private async Task UploadToStorageAsync(string csvContent, string storagePath, CancellationToken ct)
    {
        var supabaseUrl = configuration["Supabase:Url"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("Supabase:Url not configured");
        var serviceKey = configuration["Supabase:ServiceKey"]
            ?? throw new InvalidOperationException("Supabase:ServiceKey not configured");

        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
        client.DefaultRequestHeaders.Add("apikey", serviceKey);
        client.DefaultRequestHeaders.Add("x-upsert", "true");

        var content = new StringContent(csvContent, Encoding.UTF8, "text/csv");
        var uploadUrl = $"{supabaseUrl}/storage/v1/object/{BucketName}/{storagePath}";
        var response = await client.PostAsync(uploadUrl, content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Storage upload failed: {response.StatusCode} {errorBody}");
        }
    }

    private async Task<string> CreateSignedUrlAsync(string storagePath, CancellationToken ct)
    {
        var supabaseUrl = configuration["Supabase:Url"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("Supabase:Url not configured");
        var serviceKey = configuration["Supabase:ServiceKey"]
            ?? throw new InvalidOperationException("Supabase:ServiceKey not configured");

        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
        client.DefaultRequestHeaders.Add("apikey", serviceKey);

        var signUrl = $"{supabaseUrl}/storage/v1/object/sign/{BucketName}/{storagePath}";
        var body = new StringContent(
            $$"""{"expiresIn": {{UrlExpirySeconds}}}""",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync(signUrl, body, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Signed URL creation failed: {response.StatusCode} {errorBody}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var signedPath = System.Text.Json.JsonDocument.Parse(json)
            .RootElement.GetProperty("signedURL").GetString();

        return $"{supabaseUrl}/storage/v1{signedPath}";
    }

    private async Task ExpireOldExportsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var connection = connectionFactory.CreateConnection();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE public.sponsor_analytics_exports
            SET status = 'expired', signed_url = NULL
            WHERE status = 'completed' AND url_expires_at < NOW()
            """,
            cancellationToken: ct));
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private sealed record PendingExportRow(Guid Id, Guid SponsorId, string ReportType, int PeriodDays);
}
