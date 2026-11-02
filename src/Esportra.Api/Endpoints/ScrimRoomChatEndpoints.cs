using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

/// <summary>
/// The chat in a scrim room: one inbox group conversation per scrim, with the captains and managers of
/// both teams, so they can talk and settle a time without leaving the room. It lives in the normal
/// inbox, so the messages are also there afterwards.
///
/// POST /api/scrims/sessions/{id}/conversation — get (or create) the room's conversation
/// </summary>
public static class ScrimRoomChatEndpoints
{
    public static void MapScrimRoomChatEndpoints(this WebApplication app) =>
        app.MapPost("/api/scrims/sessions/{id:guid}/conversation", OpenAsync).RequireAuthorization("Authenticated");

    private sealed record PartiesRow
    {
        public Guid Team1Id { get; init; }
        public Guid Team2Id { get; init; }
        public string? Team1Name { get; init; }
        public string? Team2Name { get; init; }
    }

    private static async Task<IResult> OpenAsync(Guid id, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();

        var parties = await conn.QuerySingleOrDefaultAsync<PartiesRow>(
            """
            SELECT ss.team1_id AS Team1Id, ss.team2_id AS Team2Id, t1.name AS Team1Name, t2.name AS Team2Name
            FROM public.scrim_sessions ss
            JOIN public.teams t1 ON t1.id = ss.team1_id
            JOIN public.teams t2 ON t2.id = ss.team2_id
            WHERE ss.id = @id
            """,
            new { id });
        if (parties is null) return Results.NotFound();

        var staff = (await conn.QueryAsync<Guid>(
            """
            SELECT DISTINCT user_id FROM public.team_members
            WHERE team_id IN (@t1, @t2) AND role IN ('captain', 'manager') AND is_active = TRUE
            """,
            new { t1 = parties.Team1Id, t2 = parties.Team2Id })).ToArray();
        if (!staff.Contains(user.UserIdGuid)) return Results.Forbid();

        var created = await conn.ExecuteScalarAsync<Guid?>(
            """
            INSERT INTO public.conversations (type, title, created_by, scrim_session_id)
            VALUES ('group', @title, @userId, @id)
            ON CONFLICT (scrim_session_id) WHERE scrim_session_id IS NOT NULL DO NOTHING
            RETURNING id
            """,
            new { id, userId = user.UserIdGuid, title = $"Scrim: {parties.Team1Name} vs {parties.Team2Name}" });
        var conversationId = created
            ?? await conn.ExecuteScalarAsync<Guid>("SELECT id FROM public.conversations WHERE scrim_session_id = @id", new { id });

        // Everyone who can act for either team is in the room's chat, including staff added since.
        await conn.ExecuteAsync(
            """
            INSERT INTO public.conversation_participants (conversation_id, user_id)
            SELECT @conversationId, uid FROM UNNEST(@staff::uuid[]) AS uid
            ON CONFLICT DO NOTHING
            """,
            new { conversationId, staff });

        if (created is not null)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.messages (conversation_id, sender_id, content, message_type)
                VALUES (@conversationId, @userId, @content, 'system')
                """,
                new
                {
                    conversationId,
                    userId = user.UserIdGuid,
                    content = $"Chat for {parties.Team1Name} vs {parties.Team2Name}. Agree a match time here.",
                });
        }
        return Results.Ok(new { conversation_id = conversationId });
    }
}
