-- Queue Now: live, swipe-style LFG matching. Sessions are ephemeral presence rows; actions hold
-- both the "seen" set (pass) and pending likes; matches persist after the session ends.
-- All writes go through the API (service connection); clients never write these tables.

CREATE TABLE IF NOT EXISTS public.lfg_swipe_sessions (
    id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id          UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    game_slug        TEXT NOT NULL,
    region           TEXT NOT NULL,
    competitive_tier INT  NOT NULL,
    mic_on           BOOLEAN NOT NULL DEFAULT false,
    slots_needed     INT  NOT NULL DEFAULT 1,
    started_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_heartbeat   TIMESTAMPTZ NOT NULL DEFAULT now(),
    ended_at         TIMESTAMPTZ  -- set when the last needed slot is filled; row is kept so stats survive
);

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_lfg_swipe_slots') THEN
    ALTER TABLE public.lfg_swipe_sessions ADD CONSTRAINT chk_lfg_swipe_slots CHECK (slots_needed BETWEEN 0 AND 4);
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'lfg_swipe_session_user_game_unique') THEN
    ALTER TABLE public.lfg_swipe_sessions
      ADD CONSTRAINT lfg_swipe_session_user_game_unique UNIQUE (user_id, game_slug);
  END IF;
END $$;

-- A session is "active" when last_heartbeat is recent; the predicate cannot live in the index
-- (now() is not immutable), so index the filter columns instead.
CREATE INDEX IF NOT EXISTS idx_lfg_swipe_sessions_pool
    ON public.lfg_swipe_sessions (game_slug, region, last_heartbeat DESC);

CREATE TABLE IF NOT EXISTS public.lfg_swipe_actions (
    session_id     UUID NOT NULL REFERENCES public.lfg_swipe_sessions(id) ON DELETE CASCADE,
    target_user_id UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    direction      TEXT NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (session_id, target_user_id)
);

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_lfg_swipe_direction') THEN
    ALTER TABLE public.lfg_swipe_actions ADD CONSTRAINT chk_lfg_swipe_direction CHECK (direction IN ('like', 'pass'));
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_lfg_swipe_actions_likes
    ON public.lfg_swipe_actions (target_user_id) WHERE direction = 'like';

CREATE TABLE IF NOT EXISTS public.lfg_swipe_matches (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user1_id        UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    user2_id        UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    game_slug       TEXT NOT NULL,
    conversation_id UUID REFERENCES public.conversations(id) ON DELETE SET NULL,
    matched_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'lfg_swipe_match_ordered') THEN
    ALTER TABLE public.lfg_swipe_matches ADD CONSTRAINT lfg_swipe_match_ordered CHECK (user1_id < user2_id);
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_lfg_swipe_matches_user1 ON public.lfg_swipe_matches (user1_id, matched_at DESC);
CREATE INDEX IF NOT EXISTS idx_lfg_swipe_matches_user2 ON public.lfg_swipe_matches (user2_id, matched_at DESC);

-- RLS (pattern of 20261104100100): default deny, owner reads, service role full, no client writes.
ALTER TABLE public.lfg_swipe_sessions ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS lfg_swipe_sessions_owner_select ON public.lfg_swipe_sessions;
CREATE POLICY lfg_swipe_sessions_owner_select ON public.lfg_swipe_sessions
    FOR SELECT TO authenticated USING (user_id = auth.uid());
DROP POLICY IF EXISTS lfg_swipe_sessions_service ON public.lfg_swipe_sessions;
CREATE POLICY lfg_swipe_sessions_service ON public.lfg_swipe_sessions
    FOR ALL TO service_role USING (true) WITH CHECK (true);
REVOKE ALL ON public.lfg_swipe_sessions FROM anon;
REVOKE INSERT, UPDATE, DELETE ON public.lfg_swipe_sessions FROM authenticated;

-- Likes stay private: no authenticated policy at all.
ALTER TABLE public.lfg_swipe_actions ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS lfg_swipe_actions_service ON public.lfg_swipe_actions;
CREATE POLICY lfg_swipe_actions_service ON public.lfg_swipe_actions
    FOR ALL TO service_role USING (true) WITH CHECK (true);
REVOKE ALL ON public.lfg_swipe_actions FROM anon, authenticated;

ALTER TABLE public.lfg_swipe_matches ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS lfg_swipe_matches_party_select ON public.lfg_swipe_matches;
CREATE POLICY lfg_swipe_matches_party_select ON public.lfg_swipe_matches
    FOR SELECT TO authenticated USING (user1_id = auth.uid() OR user2_id = auth.uid());
DROP POLICY IF EXISTS lfg_swipe_matches_service ON public.lfg_swipe_matches;
CREATE POLICY lfg_swipe_matches_service ON public.lfg_swipe_matches
    FOR ALL TO service_role USING (true) WITH CHECK (true);
REVOKE ALL ON public.lfg_swipe_matches FROM anon;
REVOKE INSERT, UPDATE, DELETE ON public.lfg_swipe_matches FROM authenticated;
