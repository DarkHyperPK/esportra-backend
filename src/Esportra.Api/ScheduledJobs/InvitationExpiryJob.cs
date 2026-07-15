using Dapper;
using Esportra.Contracts.Database;
using Hangfire;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Per-invitation job that fires at the exact expires_at timestamp.
/// Idempotent: only marks the invitation as expired if still in 'sent' status.
/// </summary>
[Queue("default")]
public sealed class InvitationExpiryJob(
    IDbConnectionFactory db,
    ILogger<InvitationExpiryJob> logger)
{
    public async Task ExecuteAsync(Guid invitationId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        var affected = await conn.ExecuteAsync(
            """
            UPDATE tournament_invitations
            SET status = 'expired', updated_at = NOW(), expiry_job_id = NULL
            WHERE id = @invitationId AND status = 'sent'
            """,
            new { invitationId });

        if (affected > 0)
            logger.LogInformation("[InvitationExpiry] Invitation {Id} expired.", invitationId);
    }
}
