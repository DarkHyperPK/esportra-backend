-- ============================================================================
-- Migration: 20260410200300_venue_combos_menu.sql
-- Purpose:   F&B menu items and bundled combo deals (gaming + food/drink).
-- ============================================================================

-- ── Tables ──────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS venue_menu_items (
    id            UUID          PRIMARY KEY DEFAULT uuid_generate_v4(),
    venue_id      UUID          NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
    name          TEXT          NOT NULL,
    category      TEXT          NOT NULL DEFAULT 'other'
                                CHECK (category IN ('food', 'drink', 'snack', 'other')),
    price         NUMERIC(10,2) NOT NULL DEFAULT 0.00,
    is_available  BOOLEAN       NOT NULL DEFAULT true,
    sort_order    INTEGER       NOT NULL DEFAULT 0,
    created_at    TIMESTAMPTZ   NOT NULL DEFAULT now(),
    updated_at    TIMESTAMPTZ   NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS venue_combos (
    id              UUID          PRIMARY KEY DEFAULT uuid_generate_v4(),
    venue_id        UUID          NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
    name            TEXT          NOT NULL,
    description     TEXT          NOT NULL DEFAULT '',
    items           JSONB         NOT NULL DEFAULT '[]'::jsonb,
    -- items format: [{"type":"gaming","hours":3,"zone_id":"..."}, {"type":"menu_item","item_id":"...","qty":1}]
    total_price     NUMERIC(10,2) NOT NULL,
    original_price  NUMERIC(10,2) NOT NULL DEFAULT 0.00,
    is_active       BOOLEAN       NOT NULL DEFAULT true,
    sort_order      INTEGER       NOT NULL DEFAULT 0,
    created_at      TIMESTAMPTZ   NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ   NOT NULL DEFAULT now()
);

-- ── Indexes ─────────────────────────────────────────────────────────────────

CREATE INDEX IF NOT EXISTS idx_venue_menu_items_venue      ON venue_menu_items(venue_id);
CREATE INDEX IF NOT EXISTS idx_venue_menu_items_available  ON venue_menu_items(is_available);
CREATE INDEX IF NOT EXISTS idx_venue_menu_items_category   ON venue_menu_items(category);

-- Composite partial index: available items at a venue sorted for display
CREATE INDEX IF NOT EXISTS idx_venue_menu_items_venue_avail
    ON venue_menu_items(venue_id, sort_order)
    WHERE is_available = true;

CREATE INDEX IF NOT EXISTS idx_venue_combos_venue           ON venue_combos(venue_id);
CREATE INDEX IF NOT EXISTS idx_venue_combos_active          ON venue_combos(is_active);

-- Composite partial index: active combos at a venue sorted for display
CREATE INDEX IF NOT EXISTS idx_venue_combos_venue_active
    ON venue_combos(venue_id, sort_order)
    WHERE is_active = true;

-- GIN index for JSONB items search
CREATE INDEX IF NOT EXISTS idx_venue_combos_items_gin
    ON venue_combos USING gin (items);

-- ── Auto-update timestamp triggers ──────────────────────────────────────────

CREATE OR REPLACE FUNCTION update_venue_menu_items_updated_at()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_venue_menu_items_updated_at ON venue_menu_items;
CREATE TRIGGER trg_venue_menu_items_updated_at
    BEFORE UPDATE ON venue_menu_items
    FOR EACH ROW EXECUTE FUNCTION update_venue_menu_items_updated_at();

CREATE OR REPLACE FUNCTION update_venue_combos_updated_at()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_venue_combos_updated_at ON venue_combos;
CREATE TRIGGER trg_venue_combos_updated_at
    BEFORE UPDATE ON venue_combos
    FOR EACH ROW EXECUTE FUNCTION update_venue_combos_updated_at();

-- ── Row Level Security ──────────────────────────────────────────────────────

ALTER TABLE venue_menu_items ENABLE ROW LEVEL SECURITY;
ALTER TABLE venue_combos     ENABLE ROW LEVEL SECURITY;

-- ── venue_menu_items policies ───────────────────────────────────────────────

-- Service role: full access
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_menu_items_service_all' AND tablename = 'venue_menu_items'
  ) THEN
    CREATE POLICY venue_menu_items_service_all ON venue_menu_items
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;

-- Public SELECT for available items (gamers can browse the menu)
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_menu_items_public_select' AND tablename = 'venue_menu_items'
  ) THEN
    CREATE POLICY venue_menu_items_public_select ON venue_menu_items
      FOR SELECT USING (is_available = true);
  END IF;
END $$;

-- Venue owner: full CRUD
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_menu_items_owner_all' AND tablename = 'venue_menu_items'
  ) THEN
    CREATE POLICY venue_menu_items_owner_all ON venue_menu_items
      FOR ALL USING (
        EXISTS (
          SELECT 1 FROM venues v
          WHERE v.id       = venue_menu_items.venue_id
            AND v.owner_id = auth.uid()
        )
      ) WITH CHECK (
        EXISTS (
          SELECT 1 FROM venues v
          WHERE v.id       = venue_menu_items.venue_id
            AND v.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- Venue staff: SELECT + INSERT + UPDATE
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_menu_items_staff_select' AND tablename = 'venue_menu_items'
  ) THEN
    CREATE POLICY venue_menu_items_staff_select ON venue_menu_items
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = venue_menu_items.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_menu_items_staff_insert' AND tablename = 'venue_menu_items'
  ) THEN
    CREATE POLICY venue_menu_items_staff_insert ON venue_menu_items
      FOR INSERT WITH CHECK (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = venue_menu_items.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_menu_items_staff_update' AND tablename = 'venue_menu_items'
  ) THEN
    CREATE POLICY venue_menu_items_staff_update ON venue_menu_items
      FOR UPDATE USING (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = venue_menu_items.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      ) WITH CHECK (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = venue_menu_items.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

-- ── venue_combos policies ───────────────────────────────────────────────────

-- Service role: full access
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_combos_service_all' AND tablename = 'venue_combos'
  ) THEN
    CREATE POLICY venue_combos_service_all ON venue_combos
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;

-- Public SELECT for active combos (gamers can browse deals)
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_combos_public_select' AND tablename = 'venue_combos'
  ) THEN
    CREATE POLICY venue_combos_public_select ON venue_combos
      FOR SELECT USING (is_active = true);
  END IF;
END $$;

-- Venue owner: full CRUD
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_combos_owner_all' AND tablename = 'venue_combos'
  ) THEN
    CREATE POLICY venue_combos_owner_all ON venue_combos
      FOR ALL USING (
        EXISTS (
          SELECT 1 FROM venues v
          WHERE v.id       = venue_combos.venue_id
            AND v.owner_id = auth.uid()
        )
      ) WITH CHECK (
        EXISTS (
          SELECT 1 FROM venues v
          WHERE v.id       = venue_combos.venue_id
            AND v.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- Venue staff: SELECT + INSERT + UPDATE
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_combos_staff_select' AND tablename = 'venue_combos'
  ) THEN
    CREATE POLICY venue_combos_staff_select ON venue_combos
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = venue_combos.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_combos_staff_insert' AND tablename = 'venue_combos'
  ) THEN
    CREATE POLICY venue_combos_staff_insert ON venue_combos
      FOR INSERT WITH CHECK (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = venue_combos.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_combos_staff_update' AND tablename = 'venue_combos'
  ) THEN
    CREATE POLICY venue_combos_staff_update ON venue_combos
      FOR UPDATE USING (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = venue_combos.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      ) WITH CHECK (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = venue_combos.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

-- ── Grants ──────────────────────────────────────────────────────────────────

GRANT ALL ON venue_menu_items TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON venue_menu_items TO authenticated;

GRANT ALL ON venue_combos TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON venue_combos TO authenticated;
