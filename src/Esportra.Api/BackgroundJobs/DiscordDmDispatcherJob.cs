using Dapper;
using Esportra.Api.Services;

namespace Esportra.Api.BackgroundJobs;

/// <summary>
/// Background job that polls the notifications table for unsent Discord DMs
/// and dispatches them via DiscordNotificationService.
/// Runs every 5 seconds, picks up notifications created in the last 60 seconds
/// that haven't been sent yet (tracked via discord_dm_sent flag in data JSONB).
/// </summary>
public sealed class DiscordDmDispatcherJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DiscordDmDispatcherJob> _logger;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public DiscordDmDispatcherJob(IServiceProvider services, ILogger<DiscordDmDispatcherJob> logger)
    {
        _services = services;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the app time to start up
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        var discord = _services.GetRequiredService<DiscordNotificationService>();
        if (!discord.IsConfigured)
        {
            _logger.LogInformation("Discord bot not configured — DM dispatcher disabled");
            return;
        }

        _logger.LogInformation("Discord DM dispatcher started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingNotificationsAsync(discord, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Discord DM dispatcher error — will retry");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task ProcessPendingNotificationsAsync(DiscordNotificationService discord, CancellationToken ct)
    {
        var db = _services.GetRequiredService<IDbConnectionFactory>();
        using var conn = db.CreateConnection();

        // Fetch recent notifications that:
        // 1. Are for users who have discord_dm_enabled
        // 2. Haven't been sent yet (no discord_dm_sent flag)
        // 3. Were created in the last 2 minutes (avoid reprocessing old ones)
        var pending = (await conn.QueryAsync<PendingDm>(
            """
            SELECT n.id, n.user_id, n.type, n.title, n.message
            FROM notifications n
            INNER JOIN profiles p ON p.id = n.user_id
            WHERE n.created_at > NOW() - INTERVAL '2 minutes'
              AND COALESCE((p.settings->>'discord_dm_enabled')::boolean, FALSE) = TRUE
              AND COALESCE((n.data->>'discord_dm_sent')::boolean, FALSE) = FALSE
              AND n.type IN (
                  'match_ready', 'check_in_reminder', 'result_reported',
                  'result_disputed', 'dispute_resolved', 'tournament_registered',
                  'tournament_announcement', 'result_accepted', 'match_walkover'
              )
            ORDER BY n.created_at ASC
            LIMIT 20
            """
        )).ToList();

        if (pending.Count == 0) return;

        _logger.LogDebug("Processing {Count} pending Discord DMs", pending.Count);

        foreach (var dm in pending)
        {
            await discord.TrySendDmAsync(dm.User_Id, dm.Type, dm.Title ?? "", dm.Message ?? "");

            // Mark as sent so we don't resend
            await conn.ExecuteAsync(
                """
                UPDATE notifications
                SET data = COALESCE(data, '{}'::jsonb) || '{"discord_dm_sent": true}'::jsonb
                WHERE id = @id
                """,
                new { id = dm.Id });
        }
    }

    private sealed record PendingDm(Guid Id, Guid User_Id, string Type, string? Title, string? Message);
}
