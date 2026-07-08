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

    public async Task ExecuteAsync(Guid broadcastId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        var broadcast = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT * FROM broadcasts WHERE id = @broadcastId AND status = 'sending'",
            new { broadcastId });

        if (broadcast is null)
        {
            logger.LogWarning("Broadcast {BroadcastId} not found or not in 'sending' status", broadcastId);
            return;
        }

        var userIds = await GetTargetUserIds(conn, broadcast);
        var totalRecipients = userIds.Count;
        var deliveredCount = 0;

        logger.LogInformation("Starting broadcast {BroadcastId} to {Count} recipients", (object)broadcastId, (object)totalRecipients);

        // Process in batches
        foreach (var batch in userIds.Chunk(BatchSize))
        {
            foreach (var userId in batch)
            {
                try
                {
                    // Insert delivery record
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO broadcast_deliveries (broadcast_id, user_id, channel, status, delivered_at)
                        VALUES (@broadcastId, @userId, 'in_app', 'delivered', NOW())
                        ON CONFLICT (broadcast_id, user_id, channel) DO NOTHING
                        """,
                        new { broadcastId, userId });

                    // Push via SignalR if user is connected
                    await hubContext.Clients.User(userId.ToString()).SendAsync(
                        "BroadcastReceived",
                        new
                        {
                            id = broadcastId,
                            title = (string)broadcast.title,
                            content = (string)broadcast.content,
                            broadcast_type = (string)broadcast.broadcast_type,
                            priority = (string)broadcast.priority,
                            created_at = DateTime.UtcNow
                        },
                        ct);

                    deliveredCount++;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to deliver broadcast {BroadcastId} to user {UserId}",
                        (object)broadcastId, (object)userId);
                }
            }

            // Update progress periodically
            await conn.ExecuteAsync(
                "UPDATE broadcasts SET delivered_count = @count, updated_at = NOW() WHERE id = @broadcastId",
                new { broadcastId, count = deliveredCount });

            // Small delay between batches to avoid overwhelming SignalR
            if (!ct.IsCancellationRequested)
                await Task.Delay(100, ct);
        }

        // Mark as sent
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
            (object)broadcastId, (object)deliveredCount, (object)totalRecipients);
    }

    private static async Task<List<Guid>> GetTargetUserIds(System.Data.IDbConnection conn, dynamic broadcast)
    {
        string targetType = broadcast.target_type;

        return targetType switch
        {
            "all" => (await conn.QueryAsync<Guid>(
                "SELECT id FROM profiles WHERE id IS NOT NULL LIMIT 50000")).ToList(),

            "users" when broadcast.target_user_ids is Guid[] ids =>
                ids.ToList(),

            "segment" when broadcast.target_segment is not null =>
                await GetSegmentUserIds(conn, (string)broadcast.target_segment),

            _ => new List<Guid>()
        };
    }

    private static async Task<List<Guid>> GetSegmentUserIds(System.Data.IDbConnection conn, string segmentJson)
    {
        var segment = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(segmentJson);
        if (segment is null || segment.Count == 0)
            return (await conn.QueryAsync<Guid>("SELECT id FROM profiles LIMIT 50000")).ToList();

        var conditions = new List<string>();
        var p = new DynamicParameters();

        if (segment.TryGetValue("role", out var role))
        {
            conditions.Add("EXISTS (SELECT 1 FROM user_roles ur WHERE ur.user_id = p.id AND ur.role = @role AND ur.is_active)");
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
