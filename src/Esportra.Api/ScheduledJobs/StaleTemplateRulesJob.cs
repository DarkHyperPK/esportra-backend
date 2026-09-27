using Dapper;
using Esportra.Contracts.Database;
using Hangfire;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Daily job that flags tournament templates with rules older than 90 days as stale,
/// and clears the stale flag on recently updated templates.
/// </summary>
public sealed class StaleTemplateRulesJob(
    IDbConnectionFactory db,
    ILogger<StaleTemplateRulesJob> logger)
{
    [Queue("default")]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        // Flag templates with rules older than 90 days as stale
        var flagged = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE tournament_templates
            SET rules_stale = true
            WHERE rules_updated_at < now() - interval '90 days'
              AND rules_stale = false
              AND is_active = true
            """, cancellationToken: ct));

        // Clear stale flag on recently updated templates
        var cleared = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE tournament_templates
            SET rules_stale = false
            WHERE rules_updated_at >= now() - interval '90 days'
              AND rules_stale = true
            """, cancellationToken: ct));

        if (flagged > 0 || cleared > 0)
            logger.LogInformation("Stale rules check: {Flagged} flagged, {Cleared} cleared", flagged, cleared);
        else
            logger.LogInformation("StaleTemplateRulesJob: no stale templates found");
    }
}
