-- Participant membership is managed by authenticated server endpoints.
-- Client roles may read their own membership but may not add or move it.
DROP POLICY IF EXISTS cp_select ON public.conversation_participants;
CREATE POLICY cp_select ON public.conversation_participants FOR SELECT TO authenticated
    USING (user_id = auth.uid());
DROP POLICY IF EXISTS cp_insert ON public.conversation_participants;
DROP POLICY IF EXISTS cp_update_own ON public.conversation_participants;
REVOKE INSERT, UPDATE, DELETE ON public.conversation_participants FROM authenticated;

-- Qualify the outer message conversation ID to prevent a member of one
-- conversation inserting into another through the direct database API.
DROP POLICY IF EXISTS msg_insert ON public.messages;
CREATE POLICY msg_insert ON public.messages FOR INSERT TO authenticated
    WITH CHECK (sender_id = auth.uid() AND EXISTS (
        SELECT 1 FROM public.conversation_participants cp
        WHERE cp.conversation_id = messages.conversation_id
          AND cp.user_id = auth.uid() AND cp.is_active = TRUE
    ));
DROP POLICY IF EXISTS msg_update_own ON public.messages;
CREATE POLICY msg_update_own ON public.messages FOR UPDATE TO authenticated
    USING (sender_id = auth.uid() AND EXISTS (
        SELECT 1 FROM public.conversation_participants cp
        WHERE cp.conversation_id = messages.conversation_id
          AND cp.user_id = auth.uid() AND cp.is_active = TRUE
    ))
    WITH CHECK (sender_id = auth.uid() AND EXISTS (
        SELECT 1 FROM public.conversation_participants cp
        WHERE cp.conversation_id = messages.conversation_id
          AND cp.user_id = auth.uid() AND cp.is_active = TRUE
    ));
REVOKE UPDATE ON public.messages FROM authenticated;
GRANT UPDATE (content, is_edited, edited_at) ON public.messages TO authenticated;
DROP POLICY IF EXISTS msg_delete_own ON public.messages;
CREATE POLICY msg_delete_own ON public.messages FOR DELETE TO authenticated
    USING (sender_id = auth.uid() AND EXISTS (
        SELECT 1 FROM public.conversation_participants cp
        WHERE cp.conversation_id = messages.conversation_id
          AND cp.user_id = auth.uid() AND cp.is_active = TRUE
    ));
