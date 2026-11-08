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
        string? Channels,
        string? HeroImageUrl,
        string? CtaUrl,
        string? CtaLabel);

    private sealed record TargetUser(Guid Id, string? Email, string? Username, string? FullName);

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
                       channels::text, hero_image_url, cta_url, cta_label
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
                        var title = Personalize(broadcast.Title, user);
                        var content = Personalize(broadcast.Content, user);

                        await conn.ExecuteAsync(
                            """
                            INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                            VALUES (@userId, 'broadcast', @title, @message, @link, @data::jsonb, FALSE)
                            ON CONFLICT DO NOTHING
                            """,
                            new
                            {
                                userId = user.Id,
                                title,
                                message = content,
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
                                    title,
                                    message = content,
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
                                    title,
                                    content,
                                    broadcastType = broadcast.BroadcastType,
                                    priority = broadcast.Priority,
                                    heroImageUrl = broadcast.HeroImageUrl,
                                    ctaUrl = broadcast.CtaUrl,
                                    ctaLabel = broadcast.CtaLabel
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

            var finalStatus = (deliveredCount == 0 && totalRecipients > 0) ? "failed" : "sent";

            await conn.ExecuteAsync(
                """
                UPDATE broadcasts
                SET status = @finalStatus,
                    sent_at = NOW(),
                    total_recipients = @totalRecipients,
                    delivered_count = @deliveredCount,
                    updated_at = NOW()
                WHERE id = @broadcastId
                """,
                new { broadcastId, totalRecipients, deliveredCount, finalStatus });

            if (finalStatus == "failed")
                logger.LogWarning("Broadcast {BroadcastId} reached 0/{Total} deliveries — marked as failed",
                    broadcastId, totalRecipients);
            else
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
        var emailCol = needEmail ? "email" : "NULL::text AS email";
        var selectFields = $"id, {emailCol}, username, full_name";

        return targetType switch
        {
            "all" => (await conn.QueryAsync<TargetUser>(
                $"SELECT {selectFields} FROM profiles WHERE id IS NOT NULL LIMIT 50000")).ToList(),

            "users" when targetUserIds is { Length: > 0 } ids =>
                (await conn.QueryAsync<TargetUser>(
                    $"SELECT {selectFields} FROM profiles WHERE id = ANY(@ids)",
                    new { ids })).ToList(),

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

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        return (await conn.QueryAsync<TargetUser>(
            $"SELECT {selectFields} FROM profiles p {where} LIMIT 50000", p)).ToList();
    }

    private static string Personalize(string template, TargetUser user)
    {
        var firstName = user.FullName?.Split(' ')[0] ?? user.Username ?? "there";
        var result = template
            .Replace("{{first_name}}", firstName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{username}}", user.Username ?? firstName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{full_name}}", user.FullName ?? user.Username ?? firstName, StringComparison.OrdinalIgnoreCase);
        return ResolveEmoji(result);
    }

    // Slack/Discord-style shortcodes for broadcast content.
    private static readonly Dictionary<string, string> EmojiShortcodes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Status & alerts
        [":white_check_mark:"] = "✅",
        [":check:"] = "✅",
        [":x:"] = "❌",
        [":warning:"] = "⚠️",
        [":exclamation:"] = "❗",
        [":question:"] = "❓",
        [":info:"] = "ℹ️",
        [":new:"] = "🆕",
        [":lock:"] = "🔒",
        [":unlock:"] = "🔓",
        // Celebration & rewards
        [":tada:"] = "🎉",
        [":party:"] = "🥳",
        [":clap:"] = "👏",
        [":sparkles:"] = "✨",
        [":star:"] = "⭐",
        [":star2:"] = "🌟",
        [":trophy:"] = "🏆",
        [":medal:"] = "🥇",
        [":2nd_place:"] = "🥈",
        [":3rd_place:"] = "🥉",
        [":crown:"] = "👑",
        [":gem:"] = "💎",
        [":gift:"] = "🎁",
        [":100:"] = "💯",
        [":fire:"] = "🔥",
        [":boom:"] = "💥",
        // Gaming & esports
        [":controller:"] = "🎮",
        [":joystick:"] = "🕹️",
        [":dart:"] = "🎯",
        [":crossed_swords:"] = "⚔️",
        [":sword:"] = "⚔️",
        [":shield:"] = "🛡️",
        [":bow_and_arrow:"] = "🏹",
        [":skull:"] = "💀",
        // Reactions
        [":thumbsup:"] = "👍",
        [":thumbsdown:"] = "👎",
        [":wave:"] = "👋",
        [":muscle:"] = "💪",
        [":eyes:"] = "👀",
        [":heart:"] = "❤️",
        [":rocket:"] = "🚀",
        [":zap:"] = "⚡",
        [":lightning:"] = "⚡",
        // Notifications & comms
        [":bell:"] = "🔔",
        [":mega:"] = "📣",
        [":loudspeaker:"] = "🔊",
        [":calendar:"] = "📅",
        [":clock:"] = "🕐",
        [":hourglass:"] = "⏳",
        [":moneybag:"] = "💰",
        [":money:"] = "💰",
    };

    private static string ResolveEmoji(string text)
    {
        if (!text.Contains(':')) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != ':') { sb.Append(text[i++]); continue; }
            var end = text.IndexOf(':', i + 1);
            if (end < 0) { sb.Append(text[i++]); continue; }
            var candidate = text.Substring(i, end - i + 1);
            if (EmojiShortcodes.TryGetValue(candidate, out var emoji))
            {
                sb.Append(emoji);
                i = end + 1;
            }
            else { sb.Append(text[i++]); }
        }
        return sb.ToString();
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
