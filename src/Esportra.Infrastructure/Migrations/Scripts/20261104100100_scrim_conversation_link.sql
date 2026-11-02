-- The room chat of a scrim is a group conversation in the inbox, found by its session.
ALTER TABLE public.conversations
    ADD COLUMN IF NOT EXISTS scrim_session_id UUID NULL REFERENCES public.scrim_sessions(id) ON DELETE CASCADE;

CREATE UNIQUE INDEX IF NOT EXISTS uq_conversations_scrim_session
    ON public.conversations (scrim_session_id) WHERE scrim_session_id IS NOT NULL;
