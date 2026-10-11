-- ============================================================================
-- T25: Broadcast crew, talent, sponsors, automation, events, corrections
-- PROJ-057 P3 (Esportra Air broadcast production system)
--
-- Tables created (all idempotent):
--   public.broadcast_crew           — show staff with roles; dual-policy SELECT
--   public.broadcast_crew_invites   — single-use invite tokens (hash only stored)
--   public.broadcast_talent         — on-air talent roster for a show
--   public.broadcast_show_sponsors  — sponsor slots attached to a show
--   public.automation_rules         — trigger/action pairs for show automation
--   public.broadcast_events         — append-only event log (service_role writes)
--   public.broadcast_corrections    — immutable audit trail of manual corrections
--
-- Ownership model: org owner (organizations.owner_id = auth.uid()) has full
-- CRUD on all tables via a chain through broadcast_shows → organizations.
-- broadcast_crew additionally allows crew members to SELECT their own row.
-- broadcast_events is INSERT-only for service_role; authenticated can SELECT.
-- broadcast_corrections is SELECT/INSERT for authenticated; no UPDATE or DELETE.
-- broadcast_crew_invites has no UPDATE policy — claiming is service_role only.
-- Default deny on all tables.
-- ============================================================================

-- Helper subquery used throughout: "caller owns the show's org"
-- EXISTS (
--   SELECT 1 FROM broadcast_shows bs
--   JOIN organizations o ON o.id = bs.org_id
--   WHERE bs.id = <table>.show_id
--     AND o.owner_id = auth.uid()
-- )

-- ── 1. broadcast_crew ─────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.broadcast_crew (
    id         UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    show_id    UUID        NOT NULL REFERENCES public.broadcast_shows(id) ON DELETE CASCADE,
    user_id    UUID        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    role       TEXT        NOT NULL,            -- producer | observer | monitor | technical_director | replay_operator
    invited_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    joined_at  TIMESTAMPTZ,                     -- null until accepted and first connected
    is_active  BOOLEAN     NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- One role per user per show
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'broadcast_crew_show_user_key'
  ) THEN
    ALTER TABLE public.broadcast_crew
      ADD CONSTRAINT broadcast_crew_show_user_key UNIQUE (show_id, user_id);
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS broadcast_crew_show_id_idx
    ON public.broadcast_crew (show_id);

CREATE INDEX IF NOT EXISTS broadcast_crew_user_id_idx
    ON public.broadcast_crew (user_id);

ALTER TABLE public.broadcast_crew ENABLE ROW LEVEL SECURITY;

-- SELECT (org owner): full crew list for shows they own
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_crew' AND policyname = 'broadcast_crew_org_select'
  ) THEN
    CREATE POLICY "broadcast_crew_org_select" ON public.broadcast_crew
      FOR SELECT TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_crew.show_id
            AND o.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- SELECT (crew self): a crew member can read their own row
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_crew' AND policyname = 'broadcast_crew_self_select'
  ) THEN
    CREATE POLICY "broadcast_crew_self_select" ON public.broadcast_crew
      FOR SELECT TO authenticated
      USING (user_id = auth.uid());
  END IF;
END $$;

-- INSERT: org owner only
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_crew' AND policyname = 'broadcast_crew_org_insert'
  ) THEN
    CREATE POLICY "broadcast_crew_org_insert" ON public.broadcast_crew
      FOR INSERT TO authenticated
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_crew.show_id
            AND o.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- UPDATE: org owner only; WITH CHECK mirrors USING to block show_id reassignment
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_crew' AND policyname = 'broadcast_crew_org_update'
  ) THEN
    CREATE POLICY "broadcast_crew_org_update" ON public.broadcast_crew
      FOR UPDATE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_crew.show_id
            AND o.owner_id = auth.uid()
        )
      )
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_crew.show_id
            AND o.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- DELETE: org owner only
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_crew' AND policyname = 'broadcast_crew_org_delete'
  ) THEN
    CREATE POLICY "broadcast_crew_org_delete" ON public.broadcast_crew
      FOR DELETE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_crew.show_id
            AND o.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

GRANT SELECT, INSERT, UPDATE, DELETE ON public.broadcast_crew TO authenticated;
GRANT ALL ON public.broadcast_crew TO service_role;

-- ── 2. broadcast_crew_invites ─────────────────────────────────────────────────
-- Single-use invite tokens. Only token_hash (SHA-256) is stored; the raw token
-- is shown once to the org owner at creation time and never persisted.
-- No UPDATE policy — claiming is performed exclusively by service_role to
-- prevent client-side manipulation of claimed_at and claimed_by.

CREATE TABLE IF NOT EXISTS public.broadcast_crew_invites (
    id          UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    show_id     UUID        NOT NULL REFERENCES public.broadcast_shows(id) ON DELETE CASCADE,
    org_id      UUID        NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    role        TEXT        NOT NULL,
    token_hash  TEXT        NOT NULL UNIQUE,    -- SHA-256 of single-use token; raw never stored
    created_by  UUID        NOT NULL REFERENCES auth.users(id),
    expires_at  TIMESTAMPTZ NOT NULL DEFAULT (NOW() + INTERVAL '72 hours'),
    claimed_at  TIMESTAMPTZ,                    -- null until accepted
    claimed_by  UUID        REFERENCES auth.users(id),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- (token_hash) unique index is already backed by the inline UNIQUE constraint above.
CREATE INDEX IF NOT EXISTS broadcast_crew_invites_show_id_idx
    ON public.broadcast_crew_invites (show_id);

CREATE INDEX IF NOT EXISTS broadcast_crew_invites_expires_at_idx
    ON public.broadcast_crew_invites (expires_at);

ALTER TABLE public.broadcast_crew_invites ENABLE ROW LEVEL SECURITY;

-- SELECT: org owner only (token hashes are sensitive)
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_crew_invites' AND policyname = 'broadcast_crew_invites_org_select'
  ) THEN
    CREATE POLICY "broadcast_crew_invites_org_select" ON public.broadcast_crew_invites
      FOR SELECT TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_crew_invites.org_id
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
    WHERE tablename = 'broadcast_crew_invites' AND policyname = 'broadcast_crew_invites_org_insert'
  ) THEN
    CREATE POLICY "broadcast_crew_invites_org_insert" ON public.broadcast_crew_invites
      FOR INSERT TO authenticated
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_crew_invites.org_id
            AND owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- DELETE: org owner only — to revoke outstanding invites
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_crew_invites' AND policyname = 'broadcast_crew_invites_org_delete'
  ) THEN
    CREATE POLICY "broadcast_crew_invites_org_delete" ON public.broadcast_crew_invites
      FOR DELETE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_crew_invites.org_id
            AND owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- No UPDATE policy — invite rows are immutable after creation;
-- claiming (setting claimed_at and claimed_by) is performed via service_role only.

GRANT SELECT, INSERT, DELETE ON public.broadcast_crew_invites TO authenticated;
GRANT ALL ON public.broadcast_crew_invites TO service_role;

-- ── 3. broadcast_talent ───────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.broadcast_talent (
    id            UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    show_id       UUID        NOT NULL REFERENCES public.broadcast_shows(id) ON DELETE CASCADE,
    name          TEXT        NOT NULL,
    handle        TEXT,                          -- social handle
    role          TEXT        NOT NULL,          -- caster | analyst | host | interviewer | observer_on_air
    bio           TEXT,
    avatar_url    TEXT,
    display_order INTEGER     NOT NULL DEFAULT 0,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS broadcast_talent_show_order_idx
    ON public.broadcast_talent (show_id, display_order);

ALTER TABLE public.broadcast_talent ENABLE ROW LEVEL SECURITY;

-- SELECT: org owner of the show
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_talent' AND policyname = 'broadcast_talent_org_select'
  ) THEN
    CREATE POLICY "broadcast_talent_org_select" ON public.broadcast_talent
      FOR SELECT TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_talent.show_id
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
    WHERE tablename = 'broadcast_talent' AND policyname = 'broadcast_talent_org_insert'
  ) THEN
    CREATE POLICY "broadcast_talent_org_insert" ON public.broadcast_talent
      FOR INSERT TO authenticated
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_talent.show_id
            AND o.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- UPDATE: org owner of the show; WITH CHECK mirrors USING
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_talent' AND policyname = 'broadcast_talent_org_update'
  ) THEN
    CREATE POLICY "broadcast_talent_org_update" ON public.broadcast_talent
      FOR UPDATE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_talent.show_id
            AND o.owner_id = auth.uid()
        )
      )
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_talent.show_id
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
    WHERE tablename = 'broadcast_talent' AND policyname = 'broadcast_talent_org_delete'
  ) THEN
    CREATE POLICY "broadcast_talent_org_delete" ON public.broadcast_talent
      FOR DELETE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_talent.show_id
            AND o.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

GRANT SELECT, INSERT, UPDATE, DELETE ON public.broadcast_talent TO authenticated;
GRANT ALL ON public.broadcast_talent TO service_role;

-- ── 4. broadcast_show_sponsors ────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.broadcast_show_sponsors (
    id            UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    show_id       UUID        NOT NULL REFERENCES public.broadcast_shows(id) ON DELETE CASCADE,
    name          TEXT        NOT NULL,
    logo_url      TEXT,
    display_slot  TEXT        NOT NULL DEFAULT 'lower_third',  -- lower_third | scorebug | break_fullscreen | custom
    display_order INTEGER     NOT NULL DEFAULT 0,
    is_active     BOOLEAN     NOT NULL DEFAULT true,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS broadcast_show_sponsors_show_id_idx
    ON public.broadcast_show_sponsors (show_id);

ALTER TABLE public.broadcast_show_sponsors ENABLE ROW LEVEL SECURITY;

-- SELECT: org owner of the show
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_show_sponsors' AND policyname = 'broadcast_show_sponsors_org_select'
  ) THEN
    CREATE POLICY "broadcast_show_sponsors_org_select" ON public.broadcast_show_sponsors
      FOR SELECT TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_show_sponsors.show_id
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
    WHERE tablename = 'broadcast_show_sponsors' AND policyname = 'broadcast_show_sponsors_org_insert'
  ) THEN
    CREATE POLICY "broadcast_show_sponsors_org_insert" ON public.broadcast_show_sponsors
      FOR INSERT TO authenticated
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_show_sponsors.show_id
            AND o.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- UPDATE: org owner of the show; WITH CHECK mirrors USING
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_show_sponsors' AND policyname = 'broadcast_show_sponsors_org_update'
  ) THEN
    CREATE POLICY "broadcast_show_sponsors_org_update" ON public.broadcast_show_sponsors
      FOR UPDATE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_show_sponsors.show_id
            AND o.owner_id = auth.uid()
        )
      )
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_show_sponsors.show_id
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
    WHERE tablename = 'broadcast_show_sponsors' AND policyname = 'broadcast_show_sponsors_org_delete'
  ) THEN
    CREATE POLICY "broadcast_show_sponsors_org_delete" ON public.broadcast_show_sponsors
      FOR DELETE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_show_sponsors.show_id
            AND o.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

GRANT SELECT, INSERT, UPDATE, DELETE ON public.broadcast_show_sponsors TO authenticated;
GRANT ALL ON public.broadcast_show_sponsors TO service_role;

-- ── 5. automation_rules ───────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.automation_rules (
    id            UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    show_id       UUID        NOT NULL REFERENCES public.broadcast_shows(id) ON DELETE CASCADE,
    trigger_type  TEXT        NOT NULL,  -- match_started | match_ended | segment_started | segment_ended | custom
    action_type   TEXT        NOT NULL,  -- push_show_config | switch_scene | send_command | log_event
    config        JSONB       NOT NULL DEFAULT '{}',
    is_enabled    BOOLEAN     NOT NULL DEFAULT true,
    display_order INTEGER     NOT NULL DEFAULT 0,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS automation_rules_show_enabled_idx
    ON public.automation_rules (show_id, is_enabled);

ALTER TABLE public.automation_rules ENABLE ROW LEVEL SECURITY;

-- SELECT: org owner of the show
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'automation_rules' AND policyname = 'automation_rules_org_select'
  ) THEN
    CREATE POLICY "automation_rules_org_select" ON public.automation_rules
      FOR SELECT TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = automation_rules.show_id
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
    WHERE tablename = 'automation_rules' AND policyname = 'automation_rules_org_insert'
  ) THEN
    CREATE POLICY "automation_rules_org_insert" ON public.automation_rules
      FOR INSERT TO authenticated
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = automation_rules.show_id
            AND o.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- UPDATE: org owner of the show; WITH CHECK mirrors USING
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'automation_rules' AND policyname = 'automation_rules_org_update'
  ) THEN
    CREATE POLICY "automation_rules_org_update" ON public.automation_rules
      FOR UPDATE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = automation_rules.show_id
            AND o.owner_id = auth.uid()
        )
      )
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = automation_rules.show_id
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
    WHERE tablename = 'automation_rules' AND policyname = 'automation_rules_org_delete'
  ) THEN
    CREATE POLICY "automation_rules_org_delete" ON public.automation_rules
      FOR DELETE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = automation_rules.show_id
            AND o.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

GRANT SELECT, INSERT, UPDATE, DELETE ON public.automation_rules TO authenticated;
GRANT ALL ON public.automation_rules TO service_role;

-- ── 6. broadcast_events ───────────────────────────────────────────────────────
-- Append-only event log. Nodes write via BroadcastHub (service_role context);
-- web-originated events are inserted by the backend. Authenticated users may
-- only SELECT. event_id is a client-provided idempotency key that deduplicates
-- offline-queue replays.

CREATE TABLE IF NOT EXISTS public.broadcast_events (
    id         UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    show_id    UUID        NOT NULL REFERENCES public.broadcast_shows(id) ON DELETE CASCADE,
    node_id    UUID        REFERENCES public.broadcast_nodes(id) ON DELETE SET NULL,
    event_id   TEXT        NOT NULL,        -- client idempotency key; unique globally
    event_type TEXT        NOT NULL,        -- e.g. MatchResultSubmitted, ShowEvent, HoldTake
    payload    JSONB       NOT NULL DEFAULT '{}',
    source     TEXT        NOT NULL DEFAULT 'node',  -- node | web | system
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Deduplicates offline-queue replays
CREATE UNIQUE INDEX IF NOT EXISTS broadcast_events_event_id_uniq
    ON public.broadcast_events (event_id);

CREATE INDEX IF NOT EXISTS broadcast_events_show_created_idx
    ON public.broadcast_events (show_id, created_at DESC);

CREATE INDEX IF NOT EXISTS broadcast_events_node_id_idx
    ON public.broadcast_events (node_id)
    WHERE node_id IS NOT NULL;

ALTER TABLE public.broadcast_events ENABLE ROW LEVEL SECURITY;

-- SELECT: org owner of the show — read their own event log
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_events' AND policyname = 'broadcast_events_org_select'
  ) THEN
    CREATE POLICY "broadcast_events_org_select" ON public.broadcast_events
      FOR SELECT TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_events.show_id
            AND o.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- No INSERT / UPDATE / DELETE policies for authenticated — nodes and the backend
-- write through service_role which bypasses RLS entirely.

GRANT SELECT ON public.broadcast_events TO authenticated;
GRANT ALL ON public.broadcast_events TO service_role;

-- ── 7. broadcast_corrections ──────────────────────────────────────────────────
-- Immutable audit trail of manual corrections made during a live show.
-- Org owner may SELECT and INSERT. No UPDATE or DELETE — corrections are
-- permanent records of what was changed and why.

CREATE TABLE IF NOT EXISTS public.broadcast_corrections (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    show_id         UUID        NOT NULL REFERENCES public.broadcast_shows(id) ON DELETE CASCADE,
    corrected_by    UUID        NOT NULL REFERENCES auth.users(id),
    correction_type TEXT        NOT NULL,    -- score_override | segment_skip | manual_note
    target_id       UUID,                    -- optional reference to corrected entity
    target_type     TEXT,                    -- describes what target_id refers to, e.g. 'broadcast_show_matches'
    before_state    JSONB,                   -- snapshot before correction; null if no prior state
    after_state     JSONB       NOT NULL,    -- the corrected state
    reason          TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS broadcast_corrections_show_created_idx
    ON public.broadcast_corrections (show_id, created_at DESC);

ALTER TABLE public.broadcast_corrections ENABLE ROW LEVEL SECURITY;

-- SELECT: org owner of the show
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_corrections' AND policyname = 'broadcast_corrections_org_select'
  ) THEN
    CREATE POLICY "broadcast_corrections_org_select" ON public.broadcast_corrections
      FOR SELECT TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_corrections.show_id
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
    WHERE tablename = 'broadcast_corrections' AND policyname = 'broadcast_corrections_org_insert'
  ) THEN
    CREATE POLICY "broadcast_corrections_org_insert" ON public.broadcast_corrections
      FOR INSERT TO authenticated
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM broadcast_shows bs
          JOIN organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_corrections.show_id
            AND o.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- No UPDATE or DELETE policies — corrections are an immutable audit log.

GRANT SELECT, INSERT ON public.broadcast_corrections TO authenticated;
GRANT ALL ON public.broadcast_corrections TO service_role;
