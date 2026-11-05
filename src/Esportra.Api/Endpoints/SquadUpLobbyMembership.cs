using System.Data;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

/// <param name="Area">Where the player lives; when omitted, the lobby's own area is assumed (the board already filtered by it).</param>
public sealed record LobbyJoinRequest(
    [property: JsonPropertyName("role_key")] string? RoleKey = null,
    [property: JsonPropertyName("area")] string? Area = null);

/// <summary>
/// Lobby membership. Every change to a lobby runs under one advisory lock per lobby, so the last slot can't be over-filled.
/// POST {base}/{id}/requests                       — ask for a slot (optionally with a role)
/// POST {base}/{id}/requests/{userId}/accept       — host accepts; the player joins the lobby's group chat
/// POST {base}/{id}/requests/{userId}/decline      — host declines
/// POST {base}/{id}/leave                          — a member leaves (the slot reopens)
/// POST {base}/{id}/kick/{userId}                  — host removes a member (they can't request again)
/// </summary>
internal static class SquadUpLobbyMembership
{
    // Property-init so Dapper can bind text[] (constructor binding cannot, see LfgListingRow).
    private sealed record LobbyRow
    {
        public Guid Id { get; init; }
        public Guid HostId { get; init; }
        public string Title { get; init; } = "";
        public string Mode { get; init; } = "";
        public string Status { get; init; } = "";
        public int OpenSlots { get; init; }
        public Guid? ConversationId { get; init; }
        public string Region { get; init; } = "";
        public string? Area { get; init; }
        public int? CompetitiveTier { get; init; }
        public int? MinTier { get; init; }
        public int? MaxTier { get; init; }
        public string GameSlug { get; init; } = "";
    }

    public static void Map(WebApplication app, string baseRoute)
    {
        app.MapPost($"{baseRoute}/{{id:guid}}/requests", RequestAsync).RequireAuthorization("Authenticated");
        app.MapPost($"{baseRoute}/{{id:guid}}/requests/{{userId:guid}}/accept", AcceptAsync).RequireAuthorization("Authenticated");
        app.MapPost($"{baseRoute}/{{id:guid}}/requests/{{userId:guid}}/decline", DeclineAsync).RequireAuthorization("Authenticated");
        app.MapPost($"{baseRoute}/{{id:guid}}/leave", LeaveAsync).RequireAuthorization("Authenticated");
        app.MapPost($"{baseRoute}/{{id:guid}}/kick/{{userId:guid}}", KickAsync).RequireAuthorization("Authenticated");
    }

    private static async Task<(IDbTransaction Tx, LobbyRow? Lobby)> LockAsync(IDbConnection conn, Guid lobbyId)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        var tx = conn.BeginTransaction();
        await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", new { key = $"squad-up-lobby:{lobbyId}" }, tx);
        var lobby = await conn.QuerySingleOrDefaultAsync<LobbyRow>(
            """
            SELECT id, host_id, title, mode, status, open_slots, conversation_id, region, area,
                   competitive_tier, min_tier, max_tier, game_slug
            FROM public.squad_up_lobbies WHERE id = @lobbyId FOR UPDATE
            """, new { lobbyId }, tx);
        return (tx, lobby);
    }

    private static async Task<IResult> RequestAsync(
        Guid id, [FromBody] LobbyJoinRequest? req, HttpContext ctx, IDbConnectionFactory db, IHubContext<NotificationHub> hub)
    {
        if (ctx.Items["UserContext"] is not UserContext me) return Results.Unauthorized();
        var area = req?.Area?.Trim().ToLowerInvariant();
        if (area is not null && !SquadUpEligibility.Areas.Contains(area)) return Results.BadRequest(new { error = "INVALID_AREA" });
        using var conn = db.CreateConnection();
        var player = await SquadUpEligibility.LoadAsync(conn, me.UserIdGuid);
        var (tx, lobby) = await LockAsync(conn, id);
        using (tx)
        {
            if (lobby is null || lobby.HostId == me.UserIdGuid) return Results.NotFound(new { error = "LOBBY_NOT_FOUND" });
            var refused = await SquadUpEligibility.CheckAsync(conn, me.UserIdGuid, player,
                new SquadUpFilters(lobby.GameSlug, lobby.Mode, lobby.Area ?? "", null, null));
            if (refused is not null) return refused;
            var visible = await conn.ExecuteScalarAsync<bool>(
                $"""
                SELECT EXISTS (SELECT 1 FROM public.squad_up_lobbies l
                               CROSS JOIN (SELECT @game::text AS game_slug, @mode::text AS mode, @region::text AS region, @area::text AS area,
                                                  @tier::int AS competitive_tier, NULL::int AS min_tier, NULL::int AS max_tier) me
                               WHERE l.id = @id AND {SquadUpLobbySql.Open("l")} AND {LfgSwipeSql.Accepts("l", "me")}
                                 AND {LfgSwipeSql.NotBlocked("@myId", "l.host_id")})
                """, new { id, myId = me.UserIdGuid, game = lobby.GameSlug, mode = lobby.Mode, region = player.Region, area = area ?? lobby.Area, tier = player.Tier }, tx);
            if (!visible) return Results.NotFound(new { error = "LOBBY_NOT_FOUND" });
            if (await AcceptedAsync(conn, tx, id) >= lobby.OpenSlots) return Results.Conflict(new { error = "LOBBY_FULL" });
            var existing = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT status FROM public.squad_up_lobby_members WHERE lobby_id = @id AND user_id = @userId", new { id, userId = me.UserIdGuid }, tx);
            if (existing is "requested" or "accepted") return Results.Conflict(new { error = "ALREADY_REQUESTED" });
            if (existing is "kicked") return Results.Json(new { error = "REMOVED_FROM_LOBBY" }, statusCode: StatusCodes.Status403Forbidden);
            var role = string.IsNullOrWhiteSpace(req?.RoleKey) ? null : req!.RoleKey!.Trim().ToLowerInvariant();
            if (role is { Length: > 32 }) return Results.BadRequest(new { error = "INVALID_ROLE" });
            await conn.ExecuteAsync(
                """
                INSERT INTO public.squad_up_lobby_members (lobby_id, user_id, role_key, status) VALUES (@id, @userId, @role, 'requested')
                ON CONFLICT (lobby_id, user_id) DO UPDATE SET status = 'requested', role_key = EXCLUDED.role_key, created_at = now(), decided_at = NULL
                """, new { id, userId = me.UserIdGuid, role }, tx);
            var name = await conn.ExecuteScalarAsync<string?>("SELECT username FROM public.profiles WHERE id = @id", new { id = me.UserIdGuid }, tx) ?? "A player";
            var push = await SquadUpModeration.InsertAsync(conn, tx, lobby.HostId, "squad_up_lobby_request", "New lobby request",
                $"{name} wants to join {(lobby.Title.Length > 0 ? lobby.Title : "your lobby")}.", new { lobby_id = id }, "/competitive/squad-up?view=lobby");
            tx.Commit();
            await SquadUpModeration.PushAsync(hub, [push]);
        }
        await PushUpdatedAsync(conn, hub, id);
        return Results.Created($"/api/competitive/lfg/swipe/lobbies/{id}", new { status = "requested" });
    }

    private static Task<int> AcceptedAsync(IDbConnection conn, IDbTransaction tx, Guid lobbyId) =>
        conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.squad_up_lobby_members WHERE lobby_id = @lobbyId AND status = 'accepted'", new { lobbyId }, tx);

    private static async Task<IResult> AcceptAsync(Guid id, Guid userId, HttpContext ctx, IDbConnectionFactory db, IHubContext<NotificationHub> hub)
    {
        if (ctx.Items["UserContext"] is not UserContext me) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var (tx, lobby) = await LockAsync(conn, id);
        var pushes = new List<SwipePush>();
        using (tx)
        {
            if (lobby is null || lobby.HostId != me.UserIdGuid || lobby.Status != "open") return Results.NotFound(new { error = "LOBBY_NOT_FOUND" });
            if (await AcceptedAsync(conn, tx, id) >= lobby.OpenSlots) return Results.Conflict(new { error = "LOBBY_FULL" });
            var updated = await conn.ExecuteAsync(
                "UPDATE public.squad_up_lobby_members SET status = 'accepted', decided_at = now() WHERE lobby_id = @id AND user_id = @userId AND status = 'requested'",
                new { id, userId }, tx);
            if (updated == 0) return Results.NotFound(new { error = "REQUEST_NOT_FOUND" });
            var conversationId = await EnsureChatAsync(conn, tx, lobby, userId);
            pushes.Add(await SquadUpModeration.InsertAsync(conn, tx, userId, "squad_up_lobby_accepted", "You're in the lobby",
                $"You joined {(lobby.Title.Length > 0 ? lobby.Title : "the lobby")}. Say hi in the group chat.",
                new { lobby_id = id, conversation_id = conversationId }, $"/inbox/{conversationId}"));
            if (await AcceptedAsync(conn, tx, id) >= lobby.OpenSlots) pushes.AddRange(await MarkFullAsync(conn, tx, lobby, conversationId));
            await conn.ExecuteAsync("UPDATE public.squad_up_lobbies SET last_activity_at = now() WHERE id = @id", new { id }, tx);
            tx.Commit();
        }
        await SquadUpModeration.PushAsync(hub, pushes);
        await PushUpdatedAsync(conn, hub, id, userId);
        return Results.Ok(new { status = "accepted" });
    }

    /// <summary>The lobby's group chat: created on the first accept, the newly accepted player is (re)added each time.</summary>
    private static async Task<Guid> EnsureChatAsync(IDbConnection conn, IDbTransaction tx, LobbyRow lobby, Guid userId)
    {
        var conversationId = lobby.ConversationId ?? await conn.QuerySingleAsync<Guid>(
            "INSERT INTO public.conversations (type, title, created_by) VALUES ('group', @title, @hostId) RETURNING id",
            new { title = lobby.Title.Length > 0 ? lobby.Title : "Squad Up lobby", hostId = lobby.HostId }, tx);
        if (lobby.ConversationId is null)
            await conn.ExecuteAsync("UPDATE public.squad_up_lobbies SET conversation_id = @conversationId WHERE id = @id", new { conversationId, id = lobby.Id }, tx);
        foreach (var member in new[] { lobby.HostId, userId })
        {
            var reactivated = await conn.ExecuteAsync(
                "UPDATE public.conversation_participants SET is_active = TRUE WHERE conversation_id = @conversationId AND user_id = @member",
                new { conversationId, member }, tx);
            if (reactivated == 0)
                await conn.ExecuteAsync("INSERT INTO public.conversation_participants (conversation_id, user_id) VALUES (@conversationId, @member)",
                    new { conversationId, member }, tx);
        }
        return conversationId;
    }

    private static async Task<List<SwipePush>> MarkFullAsync(IDbConnection conn, IDbTransaction tx, LobbyRow lobby, Guid conversationId)
    {
        await conn.ExecuteAsync("UPDATE public.squad_up_lobbies SET status = 'full' WHERE id = @id", new { id = lobby.Id }, tx);
        await SquadUpStats.BumpAsync(conn, lobby.Region, SquadUpCounter.LobbiesFilled, tx);
        var everyone = (await conn.QueryAsync<Guid>(
            "SELECT user_id FROM public.squad_up_lobby_members WHERE lobby_id = @id AND status = 'accepted'", new { id = lobby.Id }, tx)).Append(lobby.HostId);
        var pushes = new List<SwipePush>();
        foreach (var user in everyone)
            pushes.Add(await SquadUpModeration.InsertAsync(conn, tx, user, "squad_up_lobby_full", "Lobby full. Time to queue!",
                $"{(lobby.Title.Length > 0 ? lobby.Title : "Your lobby")} has everyone it needs. Share the party code in the chat.",
                new { lobby_id = lobby.Id, conversation_id = conversationId }, $"/inbox/{conversationId}"));
        return pushes;
    }

    private static async Task<IResult> DeclineAsync(Guid id, Guid userId, HttpContext ctx, IDbConnectionFactory db, IHubContext<NotificationHub> hub)
    {
        if (ctx.Items["UserContext"] is not UserContext me) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var (tx, lobby) = await LockAsync(conn, id);
        using (tx)
        {
            if (lobby is null || lobby.HostId != me.UserIdGuid) return Results.NotFound(new { error = "LOBBY_NOT_FOUND" });
            var updated = await conn.ExecuteAsync(
                "UPDATE public.squad_up_lobby_members SET status = 'declined', decided_at = now() WHERE lobby_id = @id AND user_id = @userId AND status = 'requested'",
                new { id, userId }, tx);
            if (updated == 0) return Results.NotFound(new { error = "REQUEST_NOT_FOUND" });
            tx.Commit();
        }
        await PushUpdatedAsync(conn, hub, id, userId);
        return Results.NoContent();
    }

    private static async Task<IResult> LeaveAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, IHubContext<NotificationHub> hub)
    {
        if (ctx.Items["UserContext"] is not UserContext me) return Results.Unauthorized();
        return await RemoveAsync(id, me.UserIdGuid, "left", null, db, hub);
    }

    private static async Task<IResult> KickAsync(Guid id, Guid userId, HttpContext ctx, IDbConnectionFactory db, IHubContext<NotificationHub> hub)
    {
        if (ctx.Items["UserContext"] is not UserContext me) return Results.Unauthorized();
        return await RemoveAsync(id, userId, "kicked", me.UserIdGuid, db, hub);
    }

    /// <summary>Leave or kick: the player drops out of the chat and, if the lobby was full, it opens again.</summary>
    private static async Task<IResult> RemoveAsync(Guid id, Guid userId, string status, Guid? hostId, IDbConnectionFactory db, IHubContext<NotificationHub> hub)
    {
        using var conn = db.CreateConnection();
        var (tx, lobby) = await LockAsync(conn, id);
        using (tx)
        {
            if (lobby is null || (hostId is not null && lobby.HostId != hostId) || lobby.Status == "closed")
                return Results.NotFound(new { error = "LOBBY_NOT_FOUND" });
            var updated = await conn.ExecuteAsync(
                "UPDATE public.squad_up_lobby_members SET status = @status, decided_at = now() WHERE lobby_id = @id AND user_id = @userId AND status IN ('requested', 'accepted')",
                new { id, userId, status }, tx);
            if (updated == 0) return Results.NotFound(new { error = "NOT_A_MEMBER" });
            if (lobby.ConversationId is { } conversationId)
                await conn.ExecuteAsync(
                    "UPDATE public.conversation_participants SET is_active = FALSE WHERE conversation_id = @conversationId AND user_id = @userId",
                    new { conversationId, userId }, tx);
            await conn.ExecuteAsync(
                "UPDATE public.squad_up_lobbies SET status = 'open', last_heartbeat = now(), last_activity_at = now() WHERE id = @id AND status = 'full'",
                new { id }, tx);
            tx.Commit();
        }
        await PushUpdatedAsync(conn, hub, id, userId);
        return Results.NoContent();
    }

    /// <summary>Tells the host, the members and anyone else involved that the lobby changed, so their views refresh at once.</summary>
    internal static async Task PushUpdatedAsync(IDbConnection conn, IHubContext<NotificationHub> hub, Guid lobbyId, Guid? alsoNotify = null)
    {
        try
        {
            var users = (await conn.QueryAsync<Guid>(
                """
                SELECT host_id FROM public.squad_up_lobbies WHERE id = @lobbyId
                UNION SELECT user_id FROM public.squad_up_lobby_members WHERE lobby_id = @lobbyId AND status IN ('requested', 'accepted')
                """, new { lobbyId })).ToHashSet();
            if (alsoNotify is { } extra) users.Add(extra);
            foreach (var user in users)
                await hub.Clients.Group(NotificationHub.UserGroup(user.ToString()))
                    .SendAsync(NotificationHubEvents.SquadUpLobbyUpdated, new { lobbyId });
        }
        catch
        {
            // Non-critical: lobby views also refresh on their own every few seconds.
        }
    }
}
