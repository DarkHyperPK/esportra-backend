using Dapper;
using System.Data;
using Esportra.Api.Hubs;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Contracts.Requests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Messaging CRUD — conversations, messages, read status.
/// Replaces useMessaging.ts Supabase queries (real-time stays on ChatHub).
/// </summary>
public static class MessagingEndpoints
{
    private static async Task<string[]> ActiveParticipantsAsync(IDbConnection conn, Guid conversationId)
    {
        var users = await conn.QueryAsync<Guid>(
            "SELECT user_id FROM public.conversation_participants WHERE conversation_id = @conversationId AND is_active = TRUE",
            new { conversationId });
        return users.Select(id => id.ToString()).ToArray();
    }

    public static void MapMessagingEndpoints(this WebApplication app)
    {
        // ── GET /api/conversations ───────────────────────────────────────────
        app.MapGet("/api/conversations", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var conversations = await conn.QueryAsync<dynamic>(
                """
                SELECT c.*,
                    (SELECT COUNT(*) FROM messages m
                     WHERE m.conversation_id = c.id
                       AND m.sender_id <> @userId
                       AND m.created_at > COALESCE(
                           (SELECT last_read_at FROM conversation_participants cp2
                            WHERE cp2.conversation_id = c.id AND cp2.user_id = @userId), '1970-01-01'
                       )
                    ) AS unread_count
                FROM conversations c
                INNER JOIN conversation_participants cp ON cp.conversation_id = c.id
                WHERE cp.user_id = @userId AND cp.is_active = TRUE
                ORDER BY c.updated_at DESC
                """,
                new { userId = userCtx.UserIdGuid });

            // Fetch participants + last message per conversation
            var convList = conversations.AsList();
            var result = new List<object>();

            foreach (var conv in convList)
            {
                var convIdGuid = Guid.Parse(conv.id.ToString());

                var participants = await conn.QueryAsync<dynamic>(
                    """
                    SELECT cp.*, jsonb_build_object(
                        'id', p.id, 'username', p.username,
                        'full_name', p.full_name, 'avatar_url', p.avatar_url
                    ) AS user
                    FROM conversation_participants cp
                    LEFT JOIN profiles p ON p.id = cp.user_id
                    WHERE cp.conversation_id = @convId AND cp.is_active = TRUE
                    """,
                    new { convId = convIdGuid });

                var lastMessage = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT m.*, jsonb_build_object(
                        'id', p.id, 'username', p.username,
                        'full_name', p.full_name, 'avatar_url', p.avatar_url
                    ) AS sender
                    FROM messages m
                    LEFT JOIN profiles p ON p.id = m.sender_id
                    WHERE m.conversation_id = @convId
                    ORDER BY m.created_at DESC LIMIT 1
                    """,
                    new { convId = convIdGuid });

                result.Add(new
                {
                    conv.id,
                    conv.type,
                    conv.title,
                    conv.created_by,
                    conv.created_at,
                    conv.updated_at,
                    unread_count = (long)(conv.unread_count ?? 0L),
                    participants,
                    last_message = lastMessage,
                });
            }

            return Results.Ok(result);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/conversations/{id}/messages ─────────────────────────────
        app.MapGet("/api/conversations/{id}/messages", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            [FromQuery] int limit = 100,
            [FromQuery] int offset = 0,
            CancellationToken ct = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller is a participant of this conversation
            var isMember = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM conversation_participants WHERE conversation_id = @id AND user_id = @userId AND is_active = TRUE)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isMember) return Results.Forbid();

            var messages = await conn.QueryAsync<dynamic>(
                """
                SELECT m.*, jsonb_build_object(
                    'id', p.id, 'username', p.username,
                    'full_name', p.full_name, 'avatar_url', p.avatar_url
                ) AS sender
                FROM (SELECT * FROM messages WHERE conversation_id = @id
                      ORDER BY created_at DESC, id DESC LIMIT @limit OFFSET @offset) m
                LEFT JOIN profiles p ON p.id = m.sender_id
                ORDER BY m.created_at ASC, m.id ASC
                """,
                new { id, limit, offset });

            return Results.Ok(messages);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/conversations ──────────────────────────────────────────
        app.MapPost("/api/conversations", async (
            [FromBody] CreateConversationRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (req.Type is not ("direct" or "group") || (req.ParticipantIds ?? []).Any(id => !Guid.TryParse(id, out _)))
                return Results.BadRequest(new { error = "Invalid conversation participants." });
            if (req.Type == "direct" && (req.ParticipantIds is not { Length: 1 } || Guid.Parse(req.ParticipantIds[0]) == userCtx.UserIdGuid))
                return Results.BadRequest(new { error = "A direct conversation requires one other participant." });

            using var conn = db.CreateConnection();

            // For direct messages, check existing
            if (req.Type == "direct" && req.ParticipantIds is { Length: 1 })
            {
                var existingConvId = await conn.QuerySingleOrDefaultAsync<string?>(
                    """
                    SELECT c.id FROM conversations c
                    INNER JOIN conversation_participants cp1 ON cp1.conversation_id = c.id AND cp1.user_id = @userId AND cp1.is_active = TRUE
                    INNER JOIN conversation_participants cp2 ON cp2.conversation_id = c.id AND cp2.user_id = @otherId AND cp2.is_active = TRUE
                    WHERE c.type = 'direct' AND NOT EXISTS (
                        SELECT 1 FROM conversation_participants extra WHERE extra.conversation_id = c.id
                        AND extra.user_id NOT IN (@userId, @otherId))
                    LIMIT 1
                    """,
                    new { userId = userCtx.UserIdGuid, otherId = Guid.Parse(req.ParticipantIds[0]) });

                if (existingConvId is not null)
                {
                    var existing = await conn.QuerySingleAsync<dynamic>(
                        "SELECT id, type, title, created_by, created_at, updated_at FROM conversations WHERE id = @id", new { id = Guid.Parse(existingConvId) });
                    return Results.Ok(existing);
                }
            }

            var conversation = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO conversations (type, title, created_by)
                VALUES (@type, @title, @createdBy)
                RETURNING id, type, title, created_by, created_at
                """,
                new { type = req.Type, title = req.Title, createdBy = userCtx.UserIdGuid });

            // Add creator + participants
            var allParticipants = new HashSet<string>(req.ParticipantIds ?? []) { userCtx.UserId };
            foreach (var pid in allParticipants)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO conversation_participants (conversation_id, user_id) VALUES (@convId, @userId) ON CONFLICT DO NOTHING",
                    new { convId = Guid.Parse(conversation.id.ToString()), userId = Guid.Parse(pid) });
            }

            return Results.Created($"/api/conversations/{conversation.id}", conversation);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/conversations/{id}/messages ────────────────────────────
        app.MapPost("/api/conversations/{id}/messages", async (
            Guid id,
            [FromBody] SendMessageRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<ConversationHub> conversationHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.Content) || req.Content.Length > 5000)
                return Results.BadRequest(new { error = "Message must contain between 1 and 5000 characters." });
            if (req.MessageType is not (null or "text"))
                return Results.BadRequest(new { error = "Only text messages can be sent." });

            using var conn = db.CreateConnection();
            var sender = await conn.QuerySingleOrDefaultAsync<ConversationSenderDto>(
                """
                SELECT id::text AS Id, username AS Username, full_name AS FullName, avatar_url AS AvatarUrl
                FROM profiles WHERE id = @userId
                """,
                new { userId = userCtx.UserIdGuid });

            var message = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                INSERT INTO messages (conversation_id, sender_id, content, message_type, attachments)
                SELECT @convId, @senderId, @content, @messageType, @attachments::jsonb
                WHERE EXISTS (SELECT 1 FROM conversation_participants
                    WHERE conversation_id = @convId AND user_id = @senderId AND is_active = TRUE)
                RETURNING id, conversation_id, sender_id, content, message_type, attachments, is_edited, created_at
                """,
                new
                {
                    convId = id,
                    senderId = userCtx.UserIdGuid,
                    content = req.Content,
                    messageType = req.MessageType ?? "text",
                    attachments = req.Attachments ?? "{}",
                });

            if (message is null) return Results.Forbid();

            // Touch conversation updated_at
            await conn.ExecuteAsync(
                "UPDATE conversations SET updated_at = NOW() WHERE id = @id", new { id });

            // Broadcast to SignalR group
            var messageDto = new ConversationMessageDto(
                Id: message.id.ToString(),
                ConversationId: message.conversation_id.ToString(),
                SenderId: message.sender_id.ToString(),
                Content: (string)message.content,
                MessageType: (string)message.message_type,
                Attachments: message.attachments,
                IsEdited: (bool)message.is_edited,
                CreatedAt: (DateTime)message.created_at,
                Sender: sender);

            var recipients = await ActiveParticipantsAsync(conn, id);
            await conversationHub.Clients
                .Users(recipients)
                .SendAsync(ConversationHubEvents.MessageReceived, messageDto, ct);

            return Results.Created($"/api/conversations/{id}/messages/{message.id}", message);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/conversations/{id}/read ─────────────────────────────────
        app.MapPut("/api/conversations/{id}/read", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var updated = await conn.ExecuteAsync(
                "UPDATE conversation_participants SET last_read_at = NOW() WHERE conversation_id = @id AND user_id = @userId AND is_active = TRUE",
                new { id, userId = userCtx.UserIdGuid });

            return updated == 0 ? Results.Forbid() : Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/conversations/{id}/join ────────────────────────────────
        app.MapPost("/api/conversations/{id}/join", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var updated = await conn.ExecuteAsync(
                "UPDATE conversation_participants SET is_active = TRUE WHERE conversation_id = @id AND user_id = @userId",
                new { id, userId = userCtx.UserIdGuid });

            return updated == 0 ? Results.NotFound() : Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/conversations/{id}/leave ───────────────────────────────
        app.MapPost("/api/conversations/{id}/leave", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            await conn.ExecuteAsync(
                "UPDATE conversation_participants SET is_active = FALSE WHERE conversation_id = @id AND user_id = @userId",
                new { id, userId = userCtx.UserIdGuid });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/messages/{id} ───────────────────────────────────────────
        app.MapPut("/api/messages/{id}", async (
            Guid id,
            [FromBody] EditMessageRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<ConversationHub> conversationHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var msg = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT sender_id, conversation_id, message_type FROM messages WHERE id = @id", new { id });
            if (msg is null) return Results.NotFound();
            if ((Guid)msg.sender_id != userCtx.UserIdGuid) return Results.Forbid();
            if ((string)msg.message_type != "text") return Results.Forbid();
            var recipients = await ActiveParticipantsAsync(conn, (Guid)msg.conversation_id);
            if (!recipients.Contains(userCtx.UserIdGuid.ToString())) return Results.Forbid();

            var updated = await conn.QuerySingleAsync<dynamic>(
                """
                UPDATE messages
                SET content = @content, is_edited = TRUE, edited_at = NOW()
                WHERE id = @id
                RETURNING id, conversation_id, sender_id, content, message_type, attachments, is_edited, edited_at, created_at
                """,
                new { id, content = req.Content });

            // Broadcast edit to SignalR group
            await conversationHub.Clients
                .Users(recipients)
                .SendAsync(ConversationHubEvents.MessageEdited, new
                {
                    id = updated.id.ToString(),
                    content = (string)updated.content,
                    is_edited = (bool)updated.is_edited,
                    edited_at = updated.edited_at,
                }, ct);

            return Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/messages/{id} ────────────────────────────────────────
        app.MapDelete("/api/messages/{id}", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<ConversationHub> conversationHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var msg = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT sender_id, conversation_id, message_type FROM messages WHERE id = @id", new { id });
            if (msg is null) return Results.NotFound();
            if ((Guid)msg.sender_id != userCtx.UserIdGuid) return Results.Forbid();
            if ((string)msg.message_type != "text") return Results.Forbid();
            var recipients = await ActiveParticipantsAsync(conn, (Guid)msg.conversation_id);
            if (!recipients.Contains(userCtx.UserIdGuid.ToString())) return Results.Forbid();

            await conn.ExecuteAsync("DELETE FROM messages WHERE id = @id", new { id });

            // Broadcast delete to SignalR group
            await conversationHub.Clients
                .Users(recipients)
                .SendAsync(ConversationHubEvents.MessageDeleted, new { id }, ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");
    }
}
