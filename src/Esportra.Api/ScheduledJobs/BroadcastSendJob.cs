using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Infrastructure.Database;
using Esportra.Infrastructure.Email;
using Hangfire;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.ScheduledJobs;

[Queue("notifications")]
public sealed class BroadcastSendJob(
    IDbConnectionFactory db,
    IHubContext<NotificationHub> hubContext,
    IEmailService emailService,
    ILogger<BroadcastSendJob> logger)
{
    private const int BatchSize = 500;

    // Dapper cannot deserialize PostgreSQL array columns (UUID[], TEXT[]) into C# arrays
    // without custom type handlers. Use string? and cast to text in SQL, then parse manually.
    private sealed record BroadcastRow(
        Guid Id,
        string Title,
        string Content,
        string BroadcastType,
        string Priority,
        string TargetType,
        string? TargetUserIds,
        string? TargetSegment,
        string? Channels);

    private sealed record TargetUser(Guid Id, string? Email);

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
                       target_type, target_user_ids::text, target_segment,
                       channels::text
                FROM broadcasts
                WHERE id = @broadcastId AND status = 'sending'
                """,
                new { broadcastId });

            if (broadcast is null)
            {
                logger.LogWarning("Broadcast {BroadcastId} not found or not in 'sending' status", broadcastId);
                return;
            }

            var channels = ParseTextArray(broadcast.Channels) ?? ["in_app"];
            var sendEmail = channels.Contains("email", StringComparer.OrdinalIgnoreCase);

            var targetUserIds = ParseGuidArray(broadcast.TargetUserIds);

            List<TargetUser> users = await GetTargetUsers(conn, broadcast.TargetType,
                targetUserIds, broadcast.TargetSegment, sendEmail);
            totalRecipients = users.Count;

            logger.LogInformation("Starting broadcast {BroadcastId} to {Count} recipients (channels: {Channels})",
                broadcastId, totalRecipients, string.Join(",", channels));

            var notifLink = $"/notifications?broadcast={broadcastId}";
            var notifData = JsonSerializer.Serialize(new
            {
                broadcast_id = broadcastId,
                broadcast_type = broadcast.BroadcastType,
                priority = broadcast.Priority
            });

            foreach (var batch in users.Chunk(BatchSize))
            {
                if (ct.IsCancellationRequested) break;

                foreach (var user in batch)
                {
                    try
                    {
                        await conn.ExecuteAsync(
                            """
                            INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                            VALUES (@userId, 'broadcast', @title, @message, @link, @data::jsonb, FALSE)
                            ON CONFLICT DO NOTHING
                            """,
                            new
                            {
                                userId = user.Id,
                                title = broadcast.Title,
                                message = broadcast.Content,
                                link = notifLink,
                                data = notifData
                            });

                        await conn.ExecuteAsync(
                            """
                            INSERT INTO broadcast_deliveries (broadcast_id, user_id, channel, status, delivered_at)
                            VALUES (@broadcastId, @userId, 'in_app', 'delivered', NOW())
                            ON CONFLICT (broadcast_id, user_id, channel) DO NOTHING
                            """,
                            new { broadcastId, userId = user.Id });

                        await hubContext.Clients
                            .Group(NotificationHub.UserGroup(user.Id.ToString()))
                            .SendAsync(NotificationHubEvents.NewNotification,
                                new
                                {
                                    type = "broadcast",
                                    title = broadcast.Title,
                                    message = broadcast.Content,
                                    link = notifLink,
                                    data = new
                                    {
                                        broadcast_id = broadcastId,
                                        broadcast_type = broadcast.BroadcastType,
                                        priority = broadcast.Priority
                                    }
                                }, ct);

                        if (sendEmail && !string.IsNullOrWhiteSpace(user.Email))
                        {
                            try
                            {
                                await emailService.SendAsync(user.Email, EmailType.Broadcast, new
                                {
                                    title = broadcast.Title,
                                    content = broadcast.Content,
                                    broadcastType = broadcast.BroadcastType,
                                    priority = broadcast.Priority
                                }, ct);

                                await conn.ExecuteAsync(
                                    """
                                    INSERT INTO broadcast_deliveries (broadcast_id, user_id, channel, status, delivered_at)
                                    VALUES (@broadcastId, @userId, 'email', 'delivered', NOW())
                                    ON CONFLICT (broadcast_id, user_id, channel) DO NOTHING
                                    """,
                                    new { broadcastId, userId = user.Id });
                            }
                            catch (Exception ex)
                            {
                                logger.LogDebug(ex, "Email delivery failed for broadcast {BroadcastId} to {Email}",
                                    broadcastId, user.Email);

                                await conn.ExecuteAsync(
                                    """
                                    INSERT INTO broadcast_deliveries (broadcast_id, user_id, channel, status, error_message, delivered_at)
                                    VALUES (@broadcastId, @userId, 'email', 'failed', @error, NOW())
                                    ON CONFLICT (broadcast_id, user_id, channel) DO NOTHING
                                    """,
                                    new { broadcastId, userId = user.Id, error = ex.Message });
                            }
                        }

                        deliveredCount++;
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Failed to deliver broadcast {BroadcastId} to user {UserId}",
                            broadcastId, user.Id);
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

    private static async Task<List<TargetUser>> GetTargetUsers(
        IDbConnection conn, string targetType, Guid[]? targetUserIds,
        string? targetSegment, bool needEmail)
    {
        var selectFields = needEmail ? "id, email" : "id, NULL::text AS email";

        return targetType switch
        {
            "all" => (await conn.QueryAsync<TargetUser>(
                $"SELECT {selectFields} FROM profiles WHERE id IS NOT NULL LIMIT 50000")).ToList(),

            "users" when targetUserIds is { Length: > 0 } ids =>
                needEmail
                    ? (await conn.QueryAsync<TargetUser>(
                        "SELECT id, email FROM profiles WHERE id = ANY(@ids)",
                        new { ids })).ToList()
                    : ids.Select(id => new TargetUser(id, null)).ToList(),

            "segment" when targetSegment is not null =>
                await GetSegmentUsers(conn, targetSegment, selectFields),

            _ => []
        };
    }

    private static async Task<List<TargetUser>> GetSegmentUsers(
        IDbConnection conn, string segmentJson, string selectFields)
    {
        var segment = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(segmentJson);
        if (segment is null || segment.Count == 0)
            return (await conn.QueryAsync<TargetUser>(
                $"SELECT {selectFields} FROM profiles p LIMIT 50000")).ToList();

        var conditions = new List<string>();
        var p = new DynamicParameters();

        if (segment.TryGetValue("role", out var role))
        {
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
        return (await conn.QueryAsync<TargetUser>(
            $"SELECT {selectFields} FROM profiles p {where} LIMIT 50000", p)).ToList();
    }

    private static string[]? ParseTextArray(string? pgArray)
    {
        if (string.IsNullOrWhiteSpace(pgArray)) return null;
        var trimmed = pgArray.Trim('{', '}');
        if (string.IsNullOrEmpty(trimmed)) return [];
        return trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim('"'))
            .ToArray();
    }

    private static Guid[]? ParseGuidArray(string? pgArray)
    {
        var strings = ParseTextArray(pgArray);
        if (strings is null) return null;
        return strings
            .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .ToArray();
    }
}
