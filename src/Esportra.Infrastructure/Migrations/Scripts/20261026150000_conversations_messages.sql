-- PROJ-076 Phase 2: conversations, conversation_participants, messages tables
-- Backing store for the in-platform inbox / DM system.

-- ── conversations ─────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.conversations (
    id          UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    type        TEXT        NOT NULL DEFAULT 'direct',   -- 'direct' | 'group'
    title       TEXT,                                    -- optional; null for direct
    created_by  UUID        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_conversation_type') THEN
        ALTER TABLE public.conversations
            ADD CONSTRAINT chk_conversation_type CHECK (type IN ('direct', 'group'));
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_conversations_updated
    ON public.conversations (updated_at DESC);

ALTER TABLE public.conversations ENABLE ROW LEVEL SECURITY;

-- Participants may see the conversation
DROP POLICY IF EXISTS conv_participant_select ON public.conversations;
CREATE POLICY conv_participant_select
    ON public.conversations
    FOR SELECT
    TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.conversation_participants cp
            WHERE cp.conversation_id = id
              AND cp.user_id = auth.uid()
              AND cp.is_active = TRUE
        )
    );

-- Only the creator may insert
DROP POLICY IF EXISTS conv_creator_insert ON public.conversations;
CREATE POLICY conv_creator_insert
    ON public.conversations
    FOR INSERT
    TO authenticated
    WITH CHECK (created_by = auth.uid());

-- Participants may touch updated_at (for last-message touch)
DROP POLICY IF EXISTS conv_participant_update ON public.conversations;
CREATE POLICY conv_participant_update
    ON public.conversations
    FOR UPDATE
    TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.conversation_participants cp
            WHERE cp.conversation_id = id
              AND cp.user_id = auth.uid()
              AND cp.is_active = TRUE
        )
    );

DROP POLICY IF EXISTS conv_service ON public.conversations;
CREATE POLICY conv_service
    ON public.conversations FOR ALL TO service_role
    USING (true) WITH CHECK (true);

-- ── conversation_participants ─────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.conversation_participants (
    conversation_id UUID        NOT NULL REFERENCES public.conversations(id) ON DELETE CASCADE,
    user_id         UUID        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    last_read_at    TIMESTAMPTZ,
    is_active       BOOLEAN     NOT NULL DEFAULT TRUE,
    joined_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (conversation_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_cp_user
    ON public.conversation_participants (user_id)
    WHERE is_active = TRUE;

ALTER TABLE public.conversation_participants ENABLE ROW LEVEL SECURITY;

-- Participants may read the participant list for conversations they're in
DROP POLICY IF EXISTS cp_select ON public.conversation_participants;
CREATE POLICY cp_select
    ON public.conversation_participants
    FOR SELECT
    TO authenticated
    USING (
        user_id = auth.uid()
        OR EXISTS (
            SELECT 1 FROM public.conversation_participants cp2
            WHERE cp2.conversation_id = conversation_id
              AND cp2.user_id = auth.uid()
              AND cp2.is_active = TRUE
        )
    );

-- Service role inserts participants (accept endpoint creates the conversation atomically)
DROP POLICY IF EXISTS cp_insert ON public.conversation_participants;
CREATE POLICY cp_insert
    ON public.conversation_participants
    FOR INSERT
    TO authenticated
    WITH CHECK (user_id = auth.uid());

-- Participant may update own row (last_read_at, is_active)
DROP POLICY IF EXISTS cp_update_own ON public.conversation_participants;
CREATE POLICY cp_update_own
    ON public.conversation_participants
    FOR UPDATE
    TO authenticated
    USING (user_id = auth.uid());

DROP POLICY IF EXISTS cp_service ON public.conversation_participants;
CREATE POLICY cp_service
    ON public.conversation_participants FOR ALL TO service_role
    USING (true) WITH CHECK (true);

-- ── messages ──────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.messages (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    conversation_id UUID        NOT NULL REFERENCES public.conversations(id) ON DELETE CASCADE,
    sender_id       UUID        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    content         TEXT        NOT NULL,
    message_type    TEXT        NOT NULL DEFAULT 'text',
    attachments     JSONB       NOT NULL DEFAULT '{}',
    is_edited       BOOLEAN     NOT NULL DEFAULT FALSE,
    edited_at       TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_message_type') THEN
        ALTER TABLE public.messages
            ADD CONSTRAINT chk_message_type CHECK (message_type IN ('text', 'system'));
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_messages_conv_created
    ON public.messages (conversation_id, created_at ASC);

ALTER TABLE public.messages ENABLE ROW LEVEL SECURITY;

-- Participants in the conversation may read messages
DROP POLICY IF EXISTS msg_select ON public.messages;
CREATE POLICY msg_select
    ON public.messages
    FOR SELECT
    TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.conversation_participants cp
            WHERE cp.conversation_id = messages.conversation_id
              AND cp.user_id = auth.uid()
              AND cp.is_active = TRUE
        )
    );

-- Only the sender may insert their own messages
DROP POLICY IF EXISTS msg_insert ON public.messages;
CREATE POLICY msg_insert
    ON public.messages
    FOR INSERT
    TO authenticated
    WITH CHECK (
        sender_id = auth.uid()
        AND EXISTS (
            SELECT 1 FROM public.conversation_participants cp
            WHERE cp.conversation_id = conversation_id
              AND cp.user_id = auth.uid()
              AND cp.is_active = TRUE
        )
    );

-- Only the sender may edit their own messages
DROP POLICY IF EXISTS msg_update_own ON public.messages;
CREATE POLICY msg_update_own
    ON public.messages
    FOR UPDATE
    TO authenticated
    USING (sender_id = auth.uid());

-- Only the sender may delete their own messages
DROP POLICY IF EXISTS msg_delete_own ON public.messages;
CREATE POLICY msg_delete_own
    ON public.messages
    FOR DELETE
    TO authenticated
    USING (sender_id = auth.uid());

DROP POLICY IF EXISTS msg_service ON public.messages;
CREATE POLICY msg_service
    ON public.messages FOR ALL TO service_role
    USING (true) WITH CHECK (true);
