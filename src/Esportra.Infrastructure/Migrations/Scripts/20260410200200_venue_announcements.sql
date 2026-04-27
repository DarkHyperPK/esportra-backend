-- ============================================================================
-- Migration: 20260410200200_venue_announcements.sql
-- Purpose:   Venue announcements — promos, events, alerts, and general info
--            displayed to gamers and agents.
-- ============================================================================

-- ── Table ───────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS venue_announcements (
    id          UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
    venue_id    UUID        NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
    title       TEXT        NOT NULL,
    body        TEXT        NOT NULL DEFAULT '',
    type        TEXT        NOT NULL DEFAULT 'info'
                            CHECK (type IN ('info', 'promo', 'event', 'alert')),
    priority    INTEGER     NOT NULL DEFAULT 0,
    is_active   BOOLEAN     NOT NULL DEFAULT true,
    starts_at   TIMESTAMPTZ,
    expires_at  TIMESTAMPTZ,
    created_by  UUID        REFERENCES auth.users(id),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- ── Indexes ─────────────────────────────────────────────────────────────────

CREATE INDEX IF NOT EXISTS idx_venue_announcements_venue     ON venue_announcements(venue_id);
CREATE INDEX IF NOT EXISTS idx_venue_announcements_active    ON venue_announcements(is_active);
CREATE INDEX IF NOT EXISTS idx_venue_announcements_starts    ON venue_announcements(starts_at);
CREATE INDEX IF NOT EXISTS idx_venue_announcements_expires   ON venue_announcements(expires_at);

-- Composite partial index for the common query: active announcements at a venue
CREATE INDEX IF NOT EXISTS idx_venue_announcements_venue_active
    ON venue_announcements(venue_id, priority DESC)
    WHERE is_active = true;

-- ── Auto-update timestamp trigger ───────────────────────────────────────────

CREATE OR REPLACE FUNCTION update_venue_announcements_updated_at()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_venue_announcements_updated_at ON venue_announcements;
CREATE TRIGGER trg_venue_announcements_updated_at
    BEFORE UPDATE ON venue_announcements
    FOR EACH ROW EXECUTE FUNCTION update_venue_announcements_updated_at();

-- ── Row Level Security ──────────────────────────────────────────────────────

ALTER TABLE venue_announcements ENABLE ROW LEVEL SECURITY;

-- Service role: full access
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_announcements_service_all' AND tablename = 'venue_announcements'
  ) THEN
    CREATE POLICY venue_announcements_service_all ON venue_announcements
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;

-- Public SELECT for active announcements (gamers, agents, anyone)
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_announcements_public_select' AND tablename = 'venue_announcements'
  ) THEN
    CREATE POLICY venue_announcements_public_select ON venue_announcements
      FOR SELECT USING (is_active = true);
  END IF;
END $$;

-- Venue owner: full CRUD on their venue's announcements
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_announcements_owner_all' AND tablename = 'venue_announcements'
  ) THEN
    CREATE POLICY venue_announcements_owner_all ON venue_announcements
      FOR ALL USING (
        EXISTS (
          SELECT 1 FROM venues v
          WHERE v.id       = venue_announcements.venue_id
            AND v.owner_id = auth.uid()
        )
      ) WITH CHECK (
        EXISTS (
          SELECT 1 FROM venues v
          WHERE v.id       = venue_announcements.venue_id
            AND v.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- Venue staff (manager, cashier): SELECT + INSERT + UPDATE on their venue's announcements
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_announcements_staff_select' AND tablename = 'venue_announcements'
  ) THEN
    CREATE POLICY venue_announcements_staff_select ON venue_announcements
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = venue_announcements.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_announcements_staff_insert' AND tablename = 'venue_announcements'
  ) THEN
    CREATE POLICY venue_announcements_staff_insert ON venue_announcements
      FOR INSERT WITH CHECK (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = venue_announcements.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_announcements_staff_update' AND tablename = 'venue_announcements'
  ) THEN
    CREATE POLICY venue_announcements_staff_update ON venue_announcements
      FOR UPDATE USING (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = venue_announcements.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      ) WITH CHECK (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = venue_announcements.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

-- ── Grants ──────────────────────────────────────────────────────────────────

GRANT ALL ON venue_announcements TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON venue_announcements TO authenticated;
