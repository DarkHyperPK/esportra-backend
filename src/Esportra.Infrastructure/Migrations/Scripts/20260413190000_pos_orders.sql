-- ============================================================================
-- Migration: 20260413190000_pos_orders.sql
-- Purpose:   POS (Point of Sale) order system for venue F&B operations.
--            Tracks orders linked to sessions/stations, stock management,
--            kitchen display support, and status lifecycle.
-- ============================================================================

-- ── Extend venue_menu_items with stock & display columns ────────────────────

ALTER TABLE venue_menu_items ADD COLUMN IF NOT EXISTS image_url TEXT;
ALTER TABLE venue_menu_items ADD COLUMN IF NOT EXISTS description TEXT DEFAULT '';
ALTER TABLE venue_menu_items ADD COLUMN IF NOT EXISTS stock_count INT;  -- NULL = unlimited
ALTER TABLE venue_menu_items ADD COLUMN IF NOT EXISTS low_stock_threshold INT DEFAULT 5;

-- ── pos_orders table ────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS pos_orders (
    id              UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    venue_id        UUID          NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
    session_id      UUID          REFERENCES venue_sessions(id),
    member_id       UUID          REFERENCES members(id),
    station_id      TEXT,         -- station label for kitchen display
    items           JSONB         NOT NULL DEFAULT '[]'::jsonb,
    -- items format: [{item_id, name, category, quantity, unit_price, total}]
    subtotal        NUMERIC(10,2) NOT NULL DEFAULT 0,
    tax             NUMERIC(10,2) NOT NULL DEFAULT 0,
    discount        NUMERIC(10,2) NOT NULL DEFAULT 0,
    total           NUMERIC(10,2) NOT NULL DEFAULT 0,
    payment_method  TEXT          DEFAULT 'cash'
                                  CHECK (payment_method IN ('cash', 'card', 'balance', 'split')),
    status          TEXT          NOT NULL DEFAULT 'pending'
                                  CHECK (status IN ('pending', 'preparing', 'ready', 'delivered', 'cancelled')),
    notes           TEXT,
    created_by      UUID          NOT NULL,
    created_at      TIMESTAMPTZ   NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ   NOT NULL DEFAULT now()
);

-- ── Indexes ─────────────────────────────────────────────────────────────────

CREATE INDEX IF NOT EXISTS idx_pos_orders_venue_status
    ON pos_orders(venue_id, status);

CREATE INDEX IF NOT EXISTS idx_pos_orders_venue_session
    ON pos_orders(venue_id, session_id);

CREATE INDEX IF NOT EXISTS idx_pos_orders_created_at
    ON pos_orders(created_at DESC);

-- Partial index for kitchen display: only active orders
CREATE INDEX IF NOT EXISTS idx_pos_orders_kitchen
    ON pos_orders(venue_id, created_at)
    WHERE status IN ('pending', 'preparing');

-- ── Auto-update timestamp trigger ───────────────────────────────────────────

CREATE OR REPLACE FUNCTION update_pos_orders_updated_at()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_pos_orders_updated_at ON pos_orders;
CREATE TRIGGER trg_pos_orders_updated_at
    BEFORE UPDATE ON pos_orders
    FOR EACH ROW EXECUTE FUNCTION update_pos_orders_updated_at();

-- ── Row Level Security ──────────────────────────────────────────────────────

ALTER TABLE pos_orders ENABLE ROW LEVEL SECURITY;

-- Service role: full access
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'pos_orders_service_all' AND tablename = 'pos_orders'
  ) THEN
    CREATE POLICY pos_orders_service_all ON pos_orders
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;

-- Venue owner: full CRUD on own venue orders
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'pos_orders_owner_all' AND tablename = 'pos_orders'
  ) THEN
    CREATE POLICY pos_orders_owner_all ON pos_orders
      FOR ALL USING (
        EXISTS (
          SELECT 1 FROM venues v
          WHERE v.id       = pos_orders.venue_id
            AND v.owner_id = auth.uid()
        )
      ) WITH CHECK (
        EXISTS (
          SELECT 1 FROM venues v
          WHERE v.id       = pos_orders.venue_id
            AND v.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- Venue staff: SELECT + INSERT + UPDATE (no DELETE)
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'pos_orders_staff_select' AND tablename = 'pos_orders'
  ) THEN
    CREATE POLICY pos_orders_staff_select ON pos_orders
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = pos_orders.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'pos_orders_staff_insert' AND tablename = 'pos_orders'
  ) THEN
    CREATE POLICY pos_orders_staff_insert ON pos_orders
      FOR INSERT WITH CHECK (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = pos_orders.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'pos_orders_staff_update' AND tablename = 'pos_orders'
  ) THEN
    CREATE POLICY pos_orders_staff_update ON pos_orders
      FOR UPDATE USING (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = pos_orders.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      ) WITH CHECK (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = pos_orders.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

-- ── Grants ──────────────────────────────────────────────────────────────────

GRANT ALL ON pos_orders TO service_role;
GRANT SELECT, INSERT, UPDATE ON pos_orders TO authenticated;
