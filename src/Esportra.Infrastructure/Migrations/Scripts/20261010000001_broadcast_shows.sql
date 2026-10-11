-- ============================================================================
-- T23: Broadcast shows and show-match slots
-- PROJ-057 P3 (Esportra Air broadcast production system)
--
-- Tables created (all idempotent):
--   public.broadcast_shows        — show entities, optionally tied to a tournament
--   public.broadcast_show_matches — ordered match slots within a show
--
-- Ownership model: org owner (organizations.owner_id = auth.uid()) has full
-- CRUD on their shows. broadcast_show_matches RLS chains through broadcast_shows
-- to reach the org owner check. Default deny on all tables.
-- ============================================================================

-- ── 1. broadcast_shows ───────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.broadcast_shows (
    id            UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    org_id        UUID        NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    tournament_id UUID        REFERENCES tournaments(id) ON DELETE SET NULL,
    title         TEXT        NOT NULL,
    status        TEXT        NOT NULL DEFAULT 'draft',   -- draft | scheduled | live | ended | cancelled
    scheduled_at  TIMESTAMPTZ,
    started_at    TIMESTAMPTZ,
    ended_at      TIMESTAMPTZ,
    config        JSONB       NOT NULL DEFAULT '{}',       -- freeform show-level configuration
    created_by    UUID        NOT NULL REFERENCES auth.users(id),
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS broadcast_shows_org_id_idx
    ON public.broadcast_shows (org_id);

CREATE INDEX IF NOT EXISTS broadcast_shows_tournament_id_idx
    ON public.broadcast_shows (tournament_id)
    WHERE tournament_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS broadcast_shows_status_idx
    ON public.broadcast_shows (status);

CREATE INDEX IF NOT EXISTS broadcast_shows_org_status_idx
    ON public.broadcast_shows (org_id, status);

ALTER TABLE public.broadcast_shows ENABLE ROW LEVEL SECURITY;

-- SELECT: org owner only
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_shows' AND policyname = 'broadcast_shows_org_select'
  ) THEN
    CREATE POLICY "broadcast_shows_org_select" ON public.broadcast_shows
      FOR SELECT TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_shows.org_id
            AND owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- INSERT: org owner only
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_shows' AND policyname = 'broadcast_shows_org_insert'
  ) THEN
    CREATE POLICY "broadcast_shows_org_insert" ON public.broadcast_shows
      FOR INSERT TO authenticated
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_shows.org_id
            AND owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- UPDATE: org owner only; WITH CHECK mirrors USING to prevent org_id reassignment
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_shows' AND policyname = 'broadcast_shows_org_update'
  ) THEN
    CREATE POLICY "broadcast_shows_org_update" ON public.broadcast_shows
      FOR UPDATE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_shows.org_id
            AND owner_id = auth.uid()
        )
      )
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_shows.org_id
            AND owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- DELETE: org owner only
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_shows' AND policyname = 'broadcast_shows_org_delete'
  ) THEN
    CREATE POLICY "broadcast_shows_org_delete" ON public.broadcast_shows
      FOR DELETE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_shows.org_id
            AND owner_id = auth.uid()
        )
      );
  END IF;
END $$;

GRANT SELECT, INSERT, UPDATE, DELETE ON public.broadcast_shows TO authenticated;
GRANT ALL ON public.broadcast_shows TO service_role;

-- ── 2. broadcast_show_matches ─────────────────────────────────────────────────
-- Ordered match slots within a show. Each match appears at most once per show
-- and each match_code (HUD slot identifier) is unique per show.
-- RLS chains through broadcast_shows to the org owner.

CREATE TABLE IF NOT EXISTS public.broadcast_show_matches (
    id            UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    show_id       UUID        NOT NULL REFERENCES public.broadcast_shows(id) ON DELETE CASCADE,
    match_id      UUID        NOT NULL REFERENCES brkt_matches(id) ON DELETE CASCADE,
    display_order INTEGER     NOT NULL DEFAULT 0,
    status        TEXT        NOT NULL DEFAULT 'pending',  -- pending | live | completed | skipped
    match_code    TEXT        NOT NULL DEFAULT 'MAIN',     -- e.g. MAIN, GF1 — HUD match code for this slot
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- A match appears in a show at most once
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'broadcast_show_matches_show_match_key'
  ) THEN
    ALTER TABLE public.broadcast_show_matches
      ADD CONSTRAINT broadcast_show_matches_show_match_key UNIQUE (show_id, match_id);
  END IF;
END $$;

-- Match codes are unique per show
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'broadcast_show_matches_show_code_key'
  ) THEN
    ALTER TABLE public.broadcast_show_matches
      ADD CONSTRAINT broadcast_show_matches_show_code_key UNIQUE (show_id, match_code);
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS broadcast_show_matches_show_order_idx
    ON public.broadcast_show_matches (show_id, display_order);

ALTER TABLE public.broadcast_show_matches ENABLE ROW LEVEL SECURITY;

-- SELECT: org owner of the show (chained through broadcast_shows)
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_show_matches' AND policyname = 'broadcast_show_matches_org_select'
  ) THEN
    CREATE POLICY "broadcast_show_matches_org_select" ON public.broadcast_show_matches
      FOR SELECT TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_show_matches.show_id
            AND o.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- INSERT: org owner of the show
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_show_matches' AND policyname = 'broadcast_show_matches_org_insert'
  ) THEN
    CREATE POLICY "broadcast_show_matches_org_insert" ON public.broadcast_show_matches
      FOR INSERT TO authenticated
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_show_matches.show_id
            AND o.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- UPDATE: org owner of the show; WITH CHECK mirrors USING to block show_id reassignment
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_show_matches' AND policyname = 'broadcast_show_matches_org_update'
  ) THEN
    CREATE POLICY "broadcast_show_matches_org_update" ON public.broadcast_show_matches
      FOR UPDATE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_show_matches.show_id
            AND o.owner_id = auth.uid()
        )
      )
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_show_matches.show_id
            AND o.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- DELETE: org owner of the show
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_show_matches' AND policyname = 'broadcast_show_matches_org_delete'
  ) THEN
    CREATE POLICY "broadcast_show_matches_org_delete" ON public.broadcast_show_matches
      FOR DELETE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_show_matches.show_id
            AND o.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

GRANT SELECT, INSERT, UPDATE, DELETE ON public.broadcast_show_matches TO authenticated;
GRANT ALL ON public.broadcast_show_matches TO service_role;
