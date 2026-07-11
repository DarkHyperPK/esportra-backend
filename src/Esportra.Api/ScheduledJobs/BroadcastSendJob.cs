using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Infrastructure.Database;
using Hangfire;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.ScheduledJobs;

[Queue("notifications")]
public sealed class BroadcastSendJob(
    IDbConnectionFactory db,
    IHubContext<NotificationHub> hubContext,
    ILogger<BroadcastSendJob> logger)
{
    private const int BatchSize = 500;

    private sealed record BroadcastRow(
        Guid Id,
        string Title,
        string Content,
        string BroadcastType,
        string Priority,
        string TargetType,
        Guid[]? TargetUserIds,
        string? TargetSegment);

    public async Task ExecuteAsync(Guid broadcastId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var deliveredCount = 0;
        var totalRecipients = 0;

        try
        {
            var broadcast = await conn.QuerySingleOrDefaultAsync<BroadcastRow>(
                """
                SELECT id, title, content, broadcast_type, priority,
                       target_type, target_user_ids, target_segment
                FROM broadcasts
                WHERE id = @broadcastId AND status = 'sending'
                """,
                new { broadcastId });

            if (broadcast is null)
            {
                logger.LogWarning("Broadcast {BroadcastId} not found or not in 'sending' status", broadcastId);
                return;
            }

            List<Guid> userIds = await GetTargetUserIds(conn, broadcast);
            totalRecipients = userIds.Count;

            logger.LogInformation("Starting broadcast {BroadcastId} to {Count} recipients",
                broadcastId, totalRecipients);

            foreach (var batch in userIds.Chunk(BatchSize))
            {
                if (ct.IsCancellationRequested) break;

                foreach (var userId in batch)
                {
                    try
                    {
                        await conn.ExecuteAsync(
                            """
                            INSERT INTO broadcast_deliveries (broadcast_id, user_id, channel, status, delivered_at)
                            VALUES (@broadcastId, @userId, 'in_app', 'delivered', NOW())
                            ON CONFLICT (broadcast_id, user_id, channel) DO NOTHING
                            """,
                            new { broadcastId, userId });

                        await hubContext.Clients.User(userId.ToString()).SendAsync(
                            "BroadcastReceived",
                            new
                            {
                                id = broadcastId,
                                title = broadcast.Title,
                                content = broadcast.Content,
                                broadcast_type = broadcast.BroadcastType,
                                priority = broadcast.Priority,
                                created_at = DateTime.UtcNow
                            },
                            ct);

                        deliveredCount++;
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Failed to deliver broadcast {BroadcastId} to user {UserId}",
                            broadcastId, userId);
                    }
                }

                await conn.ExecuteAsync(
                    "UPDATE broadcasts SET delivered_count = @count, updated_at = NOW() WHERE id = @broadcastId",
                    new { broadcastId, count = deliveredCount });

                if (!ct.IsCancellationRequested)
                    await Task.Delay(100, ct);
            }

            await conn.ExecuteAsync(
                """
                UPDATE broadcasts
                SET status = 'sent',
                    sent_at = NOW(),
                    total_recipients = @totalRecipients,
                    delivered_count = @deliveredCount,
                    updated_at = NOW()
                WHERE id = @broadcastId
                """,
                new { broadcastId, totalRecipients, deliveredCount });

            logger.LogInformation("Completed broadcast {BroadcastId}: {Delivered}/{Total} delivered",
                broadcastId, deliveredCount, totalRecipients);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Broadcast {BroadcastId} failed after delivering {Delivered} messages",
                broadcastId, deliveredCount);

            await conn.ExecuteAsync(
                """
                UPDATE broadcasts
                SET status = 'failed',
                    total_recipients = @totalRecipients,
                    delivered_count = @deliveredCount,
                    updated_at = NOW()
                WHERE id = @broadcastId AND status = 'sending'
                """,
                new { broadcastId, totalRecipients, deliveredCount });

            throw;
        }
    }

    private static async Task<List<Guid>> GetTargetUserIds(IDbConnection conn, BroadcastRow broadcast)
    {
        return broadcast.TargetType switch
        {
            "all" => (await conn.QueryAsync<Guid>(
                "SELECT id FROM profiles WHERE id IS NOT NULL LIMIT 50000")).ToList(),

            "users" when broadcast.TargetUserIds is { Length: > 0 } ids => ids.ToList(),

            "segment" when broadcast.TargetSegment is not null =>
                await GetSegmentUserIds(conn, broadcast.TargetSegment),

            _ => []
        };
    }

    private static async Task<List<Guid>> GetSegmentUserIds(IDbConnection conn, string segmentJson)
    {
        var segment = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(segmentJson);
        if (segment is null || segment.Count == 0)
            return (await conn.QueryAsync<Guid>("SELECT id FROM profiles LIMIT 50000")).ToList();

        var conditions = new List<string>();
        var p = new DynamicParameters();

        if (segment.TryGetValue("role", out var role))
        {
            // Use admin_user_roles + admin_roles pattern (row existence = active)
            conditions.Add("""
                EXISTS (
                    SELECT 1 FROM admin_user_roles aur
                    JOIN admin_roles ar ON ar.id = aur.role_id
                    WHERE aur.user_id = p.id AND ar.name = @role
                )
                """);
            p.Add("role", role.GetString());
        }

        if (segment.TryGetValue("country", out var country))
        {
            conditions.Add("p.country_code = @country");
            p.Add("country", country.GetString());
        }

        if (segment.TryGetValue("is_verified", out var isVerified))
        {
            conditions.Add(isVerified.GetBoolean()
                ? "p.is_verified = TRUE"
                : "(p.is_verified IS NULL OR p.is_verified = FALSE)");
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        return (await conn.QueryAsync<Guid>(
            $"SELECT p.id FROM profiles p {where} LIMIT 50000", p)).ToList();
    }
}
