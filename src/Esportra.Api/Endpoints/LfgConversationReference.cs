using System.Data;
using Dapper;
using Esportra.Api.Hubs;

namespace Esportra.Api.Endpoints;

/// <summary>Records the listing and introduction at the point a connection is accepted.</summary>
internal static class LfgConversationReference
{
    public static async Task<ConversationMessageDto?> EnsureAsync(IDbConnection conn, IDbTransaction tx, Guid interestId, Guid conversationId)
    {
        var message = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            INSERT INTO public.messages (conversation_id, sender_id, content, message_type, attachments, lfg_interest_id)
            SELECT @conversationId, i.from_user_id, COALESCE(NULLIF(btrim(i.message), ''), 'Connected through this listing.'),
                'system', jsonb_build_object('lfg_listing', jsonb_build_object(
                    'listing_id', l.id, 'game_slug', l.game_slug, 'intent', l.intent::text,
                    'name', CASE WHEN l.team_id IS NOT NULL THEN COALESCE(t.name, 'Team') ELSE COALESCE(p.username, 'Player') END,
                    'description', l.description,
                    'image_url', CASE WHEN l.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END,
                    'banner_url', CASE WHEN l.team_id IS NOT NULL THEN NULL
                        WHEN l.game_slug = 'valorant' AND pgs.player_card_id IS NOT NULL
                            THEN 'https://media.valorant-api.com/playercards/' || pgs.player_card_id || '/wideart.png'
                        ELSE p.banner_url END)), i.id
            FROM public.lfg_interests i
            JOIN public.lfg_listings l ON l.id = i.listing_id
            LEFT JOIN public.profiles p ON p.id = l.user_id
            LEFT JOIN public.teams t ON t.id = l.team_id
            LEFT JOIN public.player_game_stats pgs ON pgs.user_id = l.user_id AND pgs.game_slug = l.game_slug
                AND EXISTS (SELECT 1 FROM public.riot_accounts ra WHERE ra.user_id = l.user_id AND ra.puuid = pgs.provider_player_id)
            WHERE i.id = @interestId AND i.status = 'accepted' AND i.conversation_id = @conversationId
            ON CONFLICT (lfg_interest_id) WHERE lfg_interest_id IS NOT NULL DO NOTHING
            RETURNING id, conversation_id, sender_id, content, message_type, attachments, is_edited, created_at
            """, new { interestId, conversationId }, tx);
        if (message is null) return null;
        await conn.ExecuteAsync("UPDATE public.conversations SET updated_at = now() WHERE id = @conversationId", new { conversationId }, tx);
        return new ConversationMessageDto(message.id.ToString(), message.conversation_id.ToString(), message.sender_id.ToString(),
            (string)message.content, (string)message.message_type, message.attachments, (bool)message.is_edited, (DateTime)message.created_at, null);
    }
}
