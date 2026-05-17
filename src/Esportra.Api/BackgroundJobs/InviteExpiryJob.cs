using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Api.BackgroundJobs;

public sealed class InviteExpiryJob(
    IServiceScopeFactory scopeFactory,
    ILogger<InviteExpiryJob> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        logger.LogInformation("[InviteExpiry] Background job started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExpireInvitationsAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "[InviteExpiry] Error during poll cycle.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ExpireInvitationsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();

        using var conn = db.CreateConnection();
        var expired = await conn.ExecuteAsync(
            """
            UPDATE public.tournament_invitations
            SET status = 'expired', updated_at = NOW()
            WHERE status = 'sent'
              AND expires_at IS NOT NULL
              AND expires_at <= NOW()
            """);

        if (expired > 0)
            logger.LogInformation("[InviteExpiry] Expired {Count} tournament invitation(s).", expired);
    }
}
