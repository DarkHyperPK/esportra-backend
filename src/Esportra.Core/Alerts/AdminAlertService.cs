using Dapper;
using Esportra.Contracts.Database;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Esportra.Core.Alerts;

public enum AlertSeverity { Info, Warning, Critical }

public sealed class AdminAlertService(IDbConnectionFactory db, ILogger<AdminAlertService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>
    /// Creates an admin alert. Never throws — failures are logged as warnings.
    /// </summary>
    public async Task CreateAsync(
        string type,
        AlertSeverity severity,
        string title,
        string? message = null,
        object? data = null,
        CancellationToken ct = default)
    {
        try
        {
            using var conn = db.CreateConnection();
            var dataJson = data is not null ? JsonSerializer.Serialize(data, JsonOpts) : "{}";

            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO admin_alerts (type, severity, title, message, data)
                VALUES (@type, @severity, @title, @message, @data::jsonb)
                """, new
            {
                type,
                severity = severity.ToString().ToLowerInvariant(),
                title,
                message,
                data = dataJson
            }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create admin alert: {Type} — {Title}", type, title);
        }
    }
}
