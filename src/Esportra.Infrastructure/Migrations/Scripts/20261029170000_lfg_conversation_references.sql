-- Keep the reference after listing/request deletion; only the server may create it.
ALTER TABLE public.messages ADD COLUMN IF NOT EXISTS lfg_interest_id uuid;
CREATE UNIQUE INDEX IF NOT EXISTS idx_messages_lfg_interest
    ON public.messages (lfg_interest_id) WHERE lfg_interest_id IS NOT NULL;

DROP POLICY IF EXISTS msg_insert ON public.messages;
CREATE POLICY msg_insert ON public.messages FOR INSERT TO authenticated
    WITH CHECK (sender_id = auth.uid() AND message_type = 'text' AND lfg_interest_id IS NULL AND EXISTS (
        SELECT 1 FROM public.conversation_participants cp
        WHERE cp.conversation_id = messages.conversation_id
          AND cp.user_id = auth.uid() AND cp.is_active = TRUE
    ));
DROP POLICY IF EXISTS msg_update_own ON public.messages;
CREATE POLICY msg_update_own ON public.messages FOR UPDATE TO authenticated
    USING (sender_id = auth.uid() AND message_type = 'text' AND lfg_interest_id IS NULL AND EXISTS (
        SELECT 1 FROM public.conversation_participants cp
        WHERE cp.conversation_id = messages.conversation_id
          AND cp.user_id = auth.uid() AND cp.is_active = TRUE
    ))
    WITH CHECK (sender_id = auth.uid() AND message_type = 'text' AND lfg_interest_id IS NULL AND EXISTS (
        SELECT 1 FROM public.conversation_participants cp
        WHERE cp.conversation_id = messages.conversation_id
          AND cp.user_id = auth.uid() AND cp.is_active = TRUE
    ));
DROP POLICY IF EXISTS msg_delete_own ON public.messages;
CREATE POLICY msg_delete_own ON public.messages FOR DELETE TO authenticated
    USING (sender_id = auth.uid() AND message_type = 'text' AND lfg_interest_id IS NULL AND EXISTS (
        SELECT 1 FROM public.conversation_participants cp
        WHERE cp.conversation_id = messages.conversation_id
          AND cp.user_id = auth.uid() AND cp.is_active = TRUE
    ));

-- Backfill existing accepted connections using their acceptance timestamp.
-- Only the original two-party direct conversation qualifies. No unread/order
-- timestamps are changed on the conversation or participants. A deleted listing
-- cannot be reconstructed. Replays skip references already inserted.
INSERT INTO public.messages (conversation_id, sender_id, content, message_type, attachments, lfg_interest_id, created_at)
SELECT i.conversation_id, i.from_user_id, COALESCE(NULLIF(btrim(i.message), ''), 'Connected through this listing.'),
    'system', jsonb_build_object('lfg_listing', jsonb_build_object(
        'listing_id', l.id, 'game_slug', l.game_slug, 'intent', l.intent::text,
        'name', CASE WHEN l.team_id IS NOT NULL THEN COALESCE(t.name, 'Team') ELSE COALESCE(p.username, 'Player') END,
        'description', l.description,
        'image_url', CASE WHEN l.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END,
        'banner_url', CASE WHEN l.team_id IS NOT NULL THEN NULL
            WHEN l.game_slug = 'valorant' AND pgs.player_card_id IS NOT NULL
                THEN 'https://media.valorant-api.com/playercards/' || pgs.player_card_id || '/wideart.png'
            ELSE p.banner_url END)), i.id, i.updated_at
FROM public.lfg_interests i
JOIN public.lfg_listings l ON l.id = i.listing_id
JOIN public.conversations c ON c.id = i.conversation_id AND c.type = 'direct'
JOIN public.conversation_participants a ON a.conversation_id = c.id AND a.user_id = i.from_user_id
JOIN public.conversation_participants b ON b.conversation_id = c.id AND b.user_id = i.to_user_id
LEFT JOIN public.profiles p ON p.id = l.user_id
LEFT JOIN public.teams t ON t.id = l.team_id
LEFT JOIN public.player_game_stats pgs ON pgs.user_id = l.user_id AND pgs.game_slug = l.game_slug
    AND EXISTS (SELECT 1 FROM public.riot_accounts ra WHERE ra.user_id = l.user_id AND ra.puuid = pgs.provider_player_id)
WHERE i.status = 'accepted' AND NOT EXISTS (
    SELECT 1 FROM public.conversation_participants extra WHERE extra.conversation_id = c.id
      AND extra.user_id NOT IN (i.from_user_id, i.to_user_id))
ON CONFLICT (lfg_interest_id) WHERE lfg_interest_id IS NOT NULL DO NOTHING;
