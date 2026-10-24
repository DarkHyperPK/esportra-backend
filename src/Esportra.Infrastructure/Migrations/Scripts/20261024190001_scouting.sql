-- 20261024190001_scouting.sql
-- Scouting: shortlists, shortlist entries, private scout notes (Phase 6)

-- ── scouting_shortlists ────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.scouting_shortlists (
    id            UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    owner_user_id UUID        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    name          TEXT        NOT NULL CHECK (char_length(name) BETWEEN 1 AND 100),
    description   TEXT        CHECK (char_length(description) <= 500),
    game_slug     TEXT,
    is_private    BOOLEAN     NOT NULL DEFAULT TRUE,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_scouting_shortlists_owner_user_id
    ON public.scouting_shortlists (owner_user_id);

-- ── scouting_shortlist_entries ─────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.scouting_shortlist_entries (
    id             UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    shortlist_id   UUID        NOT NULL REFERENCES public.scouting_shortlists(id) ON DELETE CASCADE,
    player_user_id UUID        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    note           TEXT        CHECK (char_length(note) <= 300),
    added_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (shortlist_id, player_user_id)
);

CREATE INDEX IF NOT EXISTS idx_scouting_shortlist_entries_shortlist_id
    ON public.scouting_shortlist_entries (shortlist_id);

CREATE INDEX IF NOT EXISTS idx_scouting_shortlist_entries_player_user_id
    ON public.scouting_shortlist_entries (player_user_id);

-- ── scouting_notes ─────────────────────────────────────────────────────────────
-- NULLS NOT DISTINCT so that (scout, subject, NULL game) is a single unique slot
-- and ON CONFLICT (scout_user_id, subject_user_id, game_slug) inference works for
-- the all-games note where game_slug IS NULL.

CREATE TABLE IF NOT EXISTS public.scouting_notes (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    scout_user_id   UUID        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    subject_user_id UUID        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    game_slug       TEXT,
    content         TEXT        NOT NULL CHECK (char_length(content) BETWEEN 1 AND 2000),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE NULLS NOT DISTINCT (scout_user_id, subject_user_id, game_slug)
);

CREATE INDEX IF NOT EXISTS idx_scouting_notes_scout_user_id
    ON public.scouting_notes (scout_user_id);

CREATE INDEX IF NOT EXISTS idx_scouting_notes_subject_user_id
    ON public.scouting_notes (subject_user_id);

-- ── RLS ────────────────────────────────────────────────────────────────────────

ALTER TABLE public.scouting_shortlists        ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.scouting_shortlist_entries ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.scouting_notes             ENABLE ROW LEVEL SECURITY;

-- scouting_shortlists: owner all ops
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'scouting_shortlists'
          AND policyname = 'scouting_shortlists_owner_all'
    ) THEN
        CREATE POLICY scouting_shortlists_owner_all ON public.scouting_shortlists
            USING     (owner_user_id = auth.uid())
            WITH CHECK (owner_user_id = auth.uid());
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'scouting_shortlists'
          AND policyname = 'scouting_shortlists_service_role'
    ) THEN
        CREATE POLICY scouting_shortlists_service_role ON public.scouting_shortlists
            TO service_role
            USING (TRUE) WITH CHECK (TRUE);
    END IF;
END $$;

-- scouting_shortlist_entries: owner via shortlist join
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'scouting_shortlist_entries'
          AND policyname = 'scouting_shortlist_entries_owner_all'
    ) THEN
        CREATE POLICY scouting_shortlist_entries_owner_all ON public.scouting_shortlist_entries
            USING (
                EXISTS (
                    SELECT 1 FROM public.scouting_shortlists sl
                    WHERE sl.id = scouting_shortlist_entries.shortlist_id
                      AND sl.owner_user_id = auth.uid()
                )
            )
            WITH CHECK (
                EXISTS (
                    SELECT 1 FROM public.scouting_shortlists sl
                    WHERE sl.id = scouting_shortlist_entries.shortlist_id
                      AND sl.owner_user_id = auth.uid()
                )
            );
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'scouting_shortlist_entries'
          AND policyname = 'scouting_shortlist_entries_service_role'
    ) THEN
        CREATE POLICY scouting_shortlist_entries_service_role ON public.scouting_shortlist_entries
            TO service_role
            USING (TRUE) WITH CHECK (TRUE);
    END IF;
END $$;

-- scouting_notes: scout all ops
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'scouting_notes'
          AND policyname = 'scouting_notes_scout_all'
    ) THEN
        CREATE POLICY scouting_notes_scout_all ON public.scouting_notes
            USING     (scout_user_id = auth.uid())
            WITH CHECK (scout_user_id = auth.uid());
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'scouting_notes'
          AND policyname = 'scouting_notes_service_role'
    ) THEN
        CREATE POLICY scouting_notes_service_role ON public.scouting_notes
            TO service_role
            USING (TRUE) WITH CHECK (TRUE);
    END IF;
END $$;

-- ── Feature flag ───────────────────────────────────────────────────────────────

INSERT INTO public.feature_flags (key, is_enabled, description)
VALUES ('scouting', FALSE, 'Scouting shortlists and career notes')
ON CONFLICT (key) DO NOTHING;
