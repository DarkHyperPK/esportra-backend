using System.Net.Mime;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Esportra.Api.HealthChecks;

/// <summary>
/// Writes health check results as a JSON object with per-check details.
/// Suitable for both Coolify health probes and monitoring dashboards.
/// </summary>
public static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static Task WriteJson(HttpContext ctx, HealthReport report)
    {
        ctx.Response.ContentType = MediaTypeNames.Application.Json;
        ctx.Response.StatusCode = report.Status == HealthStatus.Unhealthy
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK;

        var result = new
        {
            status = report.Status.ToString(),
            duration = report.TotalDuration.TotalMilliseconds,
            timestamp = DateTime.UtcNow,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                duration = e.Value.Duration.TotalMilliseconds,
                error = e.Value.Exception?.Message,
            }),
        };

        return ctx.Response.WriteAsync(JsonSerializer.Serialize(result, _json));
    }
}
