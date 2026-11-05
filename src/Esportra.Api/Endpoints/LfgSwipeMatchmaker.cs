using System.Data;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Contracts.Competitive;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

internal sealed record SwipePush(Guid UserId, object Payload);

internal sealed record LikePush(Guid UserId, int PendingLikes);

internal sealed record SwipeOutcome(LfgSwipeResultDto Result, IReadOnlyList<SwipePush> Pushes, LikePush? LikePush = null);

/// <summary>Right-swipe transaction: records the like and, when mutual, creates the match exactly once.</summary>
internal static class LfgSwipeMatchmaker
{
    private sealed record LockedSession(Guid Id, Guid UserId, string Region);

    /// <returns>null when the target is not in the caller's live pool.</returns>
    public static async Task<SwipeOutcome?> SwipeRightAsync(IDbConnection conn, Guid myId, Guid targetId)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        // Same key as LfgExperienceEndpoints.AcceptAsync: a swipe match and an LFG accept
        // for one pair serialize and cannot create two direct conversations.
        await conn.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(LEAST(@myId::text, @targetId::text) || ':' || GREATEST(@myId::text, @targetId::text), 0))",
            new { myId, targetId }, tx);
        // Row locks in id order so concurrent swipes touching the same sessions cannot deadlock.
        var sessions = (await conn.QueryAsync<LockedSession>(
            $"""
            SELECT s.id, s.user_id, s.region FROM public.lfg_swipe_sessions s
            WHERE s.user_id IN (@myId, @targetId) AND s.game_slug = 'valorant' AND {LfgSwipeSql.Active("s")}
            ORDER BY s.id FOR UPDATE
            """, new { myId, targetId }, tx)).ToList();
        if (sessions.Count != 2 || !await conn.ExecuteScalarAsync<bool>(
                $"SELECT EXISTS ({LfgSwipeSql.PoolTarget()})", new { myId, targetId }, tx))
            return null;

        var mine = sessions.Single(s => s.UserId == myId).Id;
        var theirs = sessions.Single(s => s.UserId == targetId).Id;
        var region = sessions.Single(s => s.UserId == myId).Region;
        var newLike = await conn.ExecuteAsync(
            """
            INSERT INTO public.lfg_swipe_actions (session_id, target_user_id, direction)
            VALUES (@mine, @targetId, 'like') ON CONFLICT (session_id, target_user_id) DO NOTHING
            """, new { mine, targetId }, tx) > 0;
        if (newLike) await SquadUpStats.BumpAsync(conn, region, SquadUpCounter.SwipesRight, tx);
        var mutual = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (SELECT 1 FROM public.lfg_swipe_actions mineAct
                           WHERE mineAct.session_id = @mine AND mineAct.target_user_id = @targetId AND mineAct.direction = 'like')
               AND EXISTS (SELECT 1 FROM public.lfg_swipe_actions theirAct
                           WHERE theirAct.session_id = @theirs AND theirAct.target_user_id = @myId AND theirAct.direction = 'like')
            """, new { mine, theirs, myId, targetId }, tx);
        if (!mutual)
        {
            // Tell the liked player something happened without revealing who (identities stay hidden until a match).
            var pending = newLike
                ? await conn.ExecuteScalarAsync<int>(LfgSwipeSql.PendingLikes(), new { myId = targetId, mySession = theirs }, tx)
                : 0;
            tx.Commit();
            return new SwipeOutcome(new LfgSwipeResultDto(), [], newLike ? new LikePush(targetId, pending) : null);
        }

        var existing = await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT id FROM public.lfg_swipe_matches
            WHERE user1_id = LEAST(@myId, @targetId) AND user2_id = GREATEST(@myId, @targetId) AND game_slug = 'valorant'
              AND matched_at >= (SELECT max(started_at) FROM public.lfg_swipe_sessions WHERE id = ANY(@ids))
            ORDER BY matched_at DESC LIMIT 1
            """, new { myId, targetId, ids = new[] { mine, theirs } }, tx);
        if (existing is not null)
        {
            var again = await LoadMatchAsync(conn, tx, myId, existing.Value);
            tx.Commit();
            return new SwipeOutcome(new LfgSwipeResultDto { Matched = true, Match = again }, []);
        }
        return await CreateMatchAsync(conn, tx, myId, targetId, [mine, theirs], region);
    }

    private static async Task<SwipeOutcome> CreateMatchAsync(
        IDbConnection conn, IDbTransaction tx, Guid myId, Guid targetId, Guid[] sessionIds, string region)
    {
        var conversationId = await FindOrCreateConversationAsync(conn, tx, myId, targetId);
        var matchId = await conn.QuerySingleAsync<Guid>(
            """
            INSERT INTO public.lfg_swipe_matches (user1_id, user2_id, game_slug, conversation_id)
            VALUES (LEAST(@myId, @targetId), GREATEST(@myId, @targetId), 'valorant', @conversationId)
            RETURNING id
            """, new { myId, targetId, conversationId }, tx);
        await SquadUpStats.BumpAsync(conn, region, SquadUpCounter.Matches, tx);
        var remaining = (await conn.QueryAsync<(Guid UserId, int Slots)>(
            """
            UPDATE public.lfg_swipe_sessions
            SET slots_needed = slots_needed - 1,
                ended_at = CASE WHEN slots_needed <= 1 THEN now() ELSE ended_at END
            WHERE id = ANY(@sessionIds)
            RETURNING user_id, slots_needed
            """, new { sessionIds }, tx)).ToDictionary(r => r.UserId, r => r.Slots);
        var ended = remaining.ToDictionary(r => r.Key, r => r.Value <= 0);
        var pushes = await InsertNotificationsAsync(conn, tx, myId, targetId, matchId, conversationId, ended);
        var match = await LoadMatchAsync(conn, tx, myId, matchId);
        var stats = ended[myId] ? await conn.QuerySingleOrDefaultAsync<LfgSwipeStatsDto>(LfgSwipeSql.Stats, new { myId }, tx) : null;
        tx.Commit();
        return new SwipeOutcome(
            new LfgSwipeResultDto { Matched = true, Match = match, SessionEnded = ended[myId], Stats = stats }, pushes);
    }

    private static Task<LfgSwipeMatchDto?> LoadMatchAsync(IDbConnection conn, IDbTransaction tx, Guid myId, Guid matchId) =>
        conn.QuerySingleOrDefaultAsync<LfgSwipeMatchDto>(
            LfgSwipeSql.Matches("AND m.id = @matchId"), new { myId, matchId }, tx);

    private static async Task<Guid> FindOrCreateConversationAsync(IDbConnection conn, IDbTransaction tx, Guid a, Guid b)
    {
        var existing = await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT c.id FROM public.conversations c
            JOIN public.conversation_participants pa ON pa.conversation_id = c.id AND pa.user_id = @a AND pa.is_active
            JOIN public.conversation_participants pb ON pb.conversation_id = c.id AND pb.user_id = @b AND pb.is_active
            WHERE c.type = 'direct' AND NOT EXISTS (SELECT 1 FROM public.conversation_participants extra
                WHERE extra.conversation_id = c.id AND extra.user_id NOT IN (@a, @b))
            ORDER BY c.created_at, c.id LIMIT 1
            """, new { a, b }, tx);
        if (existing is not null) return existing.Value;
        var created = await conn.QuerySingleAsync<Guid>(
            "INSERT INTO public.conversations (type, created_by) VALUES ('direct', @a) RETURNING id", new { a }, tx);
        await conn.ExecuteAsync(
            "INSERT INTO public.conversation_participants (conversation_id, user_id) VALUES (@created, @a), (@created, @b) ON CONFLICT DO NOTHING",
            new { created, a, b }, tx);
        return created;
    }

    private static async Task<List<SwipePush>> InsertNotificationsAsync(
        IDbConnection conn, IDbTransaction tx, Guid myId, Guid targetId, Guid matchId, Guid conversationId,
        IReadOnlyDictionary<Guid, bool> ended)
    {
        var names = (await conn.QueryAsync<(Guid Id, string? Username)>(
            "SELECT id, username FROM public.profiles WHERE id IN (@myId, @targetId)", new { myId, targetId }, tx))
            .ToDictionary(r => r.Id, r => r.Username ?? "A player");
        var pushes = new List<SwipePush>();
        foreach (var (uid, other) in new[] { (myId, targetId), (targetId, myId) })
        {
            var title = "It's a match!";
            var message = $"You and {names.GetValueOrDefault(other, "a player")} want to squad up.";
            var link = $"/inbox/{conversationId}";
            var sessionEnded = ended[uid];
            var id = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                INSERT INTO public.notifications (user_id, type, title, message, link, data, is_read)
                SELECT @uid, 'lfg_swipe_match'::notification_type, @title, @message, @link,
                       jsonb_build_object('match_id', @matchId::text, 'conversation_id', @conversationId::text,
                                          'matched_user_id', @other::text, 'session_ended', @sessionEnded), FALSE
                WHERE NOT EXISTS (SELECT 1 FROM public.notifications WHERE user_id = @uid
                    AND type::text = 'lfg_swipe_match' AND data->>'match_id' = @matchId::text)
                RETURNING id
                """, new { uid, title, message, link, matchId, conversationId, other, sessionEnded }, tx);
            if (id is null) continue;
            pushes.Add(new SwipePush(uid, new
            {
                id,
                type = "lfg_swipe_match",
                title,
                message,
                link,
                data = new Dictionary<string, object>
                {
                    ["match_id"] = matchId.ToString(),
                    ["conversation_id"] = conversationId.ToString(),
                    ["matched_user_id"] = other.ToString(),
                    ["session_ended"] = sessionEnded,
                },
            }));
        }
        return pushes;
    }

    /// <summary>Real-time "someone liked you" ping; carries only the count so identities stay hidden.</summary>
    public static async Task PushLikeAsync(IHubContext<NotificationHub> hub, LikePush? like)
    {
        if (like is null) return;
        try
        {
            await hub.Clients.Group(NotificationHub.UserGroup(like.UserId.ToString()))
                .SendAsync(NotificationHubEvents.SquadUpLike, new { pendingLikes = like.PendingLikes });
        }
        catch
        {
            // Non-critical: the heartbeat reports the same count.
        }
    }

    /// <summary>Real-time fan-out after commit; the persisted rows deliver on next fetch if a push fails.</summary>
    public static async Task PushAsync(IHubContext<NotificationHub> hub, IEnumerable<SwipePush> pushes)
    {
        foreach (var push in pushes)
        {
            try
            {
                await hub.Clients.Group(NotificationHub.UserGroup(push.UserId.ToString()))
                    .SendAsync(NotificationHubEvents.NewNotification, push.Payload);
            }
            catch
            {
                // Non-critical: the notification row is already committed.
            }
        }
    }
}
