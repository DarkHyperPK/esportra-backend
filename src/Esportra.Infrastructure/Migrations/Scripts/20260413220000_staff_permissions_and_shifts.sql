-- Staff permissions (granular per role per venue) and shift tracking
-- Migration: 20260413220000_staff_permissions_and_shifts.sql

-- ============================================================================
-- 1. Extend venue_staff: add status, pin_code_hash, expand role CHECK
-- ============================================================================

-- Add status column (many existing RLS policies already reference it)
ALTER TABLE venue_staff ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'active';

-- Backfill: accepted staff → active, un-accepted → pending
UPDATE venue_staff SET status = 'active'  WHERE accepted_at IS NOT NULL AND status = 'active';
UPDATE venue_staff SET status = 'pending' WHERE accepted_at IS NULL     AND status = 'active';

-- Add PIN code hash column for kiosk/POS login
ALTER TABLE venue_staff ADD COLUMN IF NOT EXISTS pin_code_hash TEXT;

-- Expand role CHECK to include 'technician' and 'staff'
ALTER TABLE venue_staff DROP CONSTRAINT IF EXISTS venue_staff_role_check;
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.check_constraints
    WHERE constraint_name = 'venue_staff_role_check_v2'
  ) THEN
    ALTER TABLE venue_staff
      ADD CONSTRAINT venue_staff_role_check_v2
        CHECK (role IN ('owner', 'manager', 'cashier', 'technician', 'staff'));
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_venue_staff_status ON venue_staff(venue_id, status);

-- ============================================================================
-- 2. staff_permissions — granular permission matrix per role per venue
-- ============================================================================

CREATE TABLE IF NOT EXISTS staff_permissions (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  venue_id    UUID NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
  role        TEXT NOT NULL,
  permission  TEXT NOT NULL,
  UNIQUE(venue_id, role, permission)
);

CREATE INDEX IF NOT EXISTS idx_staff_permissions_venue_role ON staff_permissions(venue_id, role);

ALTER TABLE staff_permissions ENABLE ROW LEVEL SECURITY;

-- Service role: full access
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'staff_permissions_service_all' AND tablename = 'staff_permissions') THEN
    CREATE POLICY staff_permissions_service_all ON staff_permissions
      FOR ALL USING (current_setting('request.jwt.claim.role', true) = 'service_role');
  END IF;
END $$;

-- Venue owner/staff can read permissions for their venue
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'staff_permissions_staff_select' AND tablename = 'staff_permissions') THEN
    CREATE POLICY staff_permissions_staff_select ON staff_permissions
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venues WHERE venues.id = staff_permissions.venue_id AND venues.owner_id = auth.uid()
        )
        OR EXISTS (
          SELECT 1 FROM venue_staff
          WHERE venue_staff.venue_id = staff_permissions.venue_id
            AND venue_staff.user_id = auth.uid()
            AND venue_staff.status = 'active'
        )
      );
  END IF;
END $$;

GRANT ALL ON staff_permissions TO service_role;
GRANT SELECT ON staff_permissions TO authenticated;

-- ============================================================================
-- 3. staff_shifts — clock in / clock out tracking
-- ============================================================================

CREATE TABLE IF NOT EXISTS staff_shifts (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  venue_id      UUID NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
  staff_id      UUID NOT NULL REFERENCES venue_staff(id) ON DELETE CASCADE,
  staff_name    TEXT,
  clocked_in    TIMESTAMPTZ NOT NULL DEFAULT now(),
  clocked_out   TIMESTAMPTZ,
  total_minutes NUMERIC(8,2),
  notes         TEXT,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_staff_shifts_venue      ON staff_shifts(venue_id);
CREATE INDEX IF NOT EXISTS idx_staff_shifts_staff      ON staff_shifts(staff_id);
CREATE INDEX IF NOT EXISTS idx_staff_shifts_clocked_in ON staff_shifts(venue_id, clocked_in DESC);
CREATE INDEX IF NOT EXISTS idx_staff_shifts_active     ON staff_shifts(venue_id) WHERE clocked_out IS NULL;

ALTER TABLE staff_shifts ENABLE ROW LEVEL SECURITY;

-- Service role: full access
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'staff_shifts_service_all' AND tablename = 'staff_shifts') THEN
    CREATE POLICY staff_shifts_service_all ON staff_shifts
      FOR ALL USING (current_setting('request.jwt.claim.role', true) = 'service_role');
  END IF;
END $$;

-- Owner/staff can view shifts for their venue
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'staff_shifts_staff_select' AND tablename = 'staff_shifts') THEN
    CREATE POLICY staff_shifts_staff_select ON staff_shifts
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venues WHERE venues.id = staff_shifts.venue_id AND venues.owner_id = auth.uid()
        )
        OR EXISTS (
          SELECT 1 FROM venue_staff
          WHERE venue_staff.venue_id = staff_shifts.venue_id
            AND venue_staff.user_id = auth.uid()
            AND venue_staff.status = 'active'
        )
      );
  END IF;
END $$;

GRANT ALL ON staff_shifts TO service_role;
GRANT SELECT ON staff_shifts TO authenticated;

-- ============================================================================
-- 4. Seed default permissions for all existing venues
-- ============================================================================

-- All possible permissions
-- stations.view, stations.control
-- sessions.start, sessions.end, sessions.refund
-- members.view, members.create, members.ban, members.topup
-- bookings.view, bookings.create, bookings.cancel
-- pos.orders, pos.catalog
-- analytics.view, analytics.export
-- staff.manage
-- settings.edit

-- Manager gets ALL permissions
INSERT INTO staff_permissions (venue_id, role, permission)
SELECT v.id, 'manager', p.perm
FROM venues v
CROSS JOIN (VALUES
  ('stations.view'), ('stations.control'),
  ('sessions.start'), ('sessions.end'), ('sessions.refund'),
  ('members.view'), ('members.create'), ('members.ban'), ('members.topup'),
  ('bookings.view'), ('bookings.create'), ('bookings.cancel'),
  ('pos.orders'), ('pos.catalog'),
  ('analytics.view'), ('analytics.export'),
  ('staff.manage'),
  ('settings.edit')
) AS p(perm)
WHERE v.deleted_at IS NULL
ON CONFLICT (venue_id, role, permission) DO NOTHING;

-- Cashier gets same as manager (they handle transactions)
INSERT INTO staff_permissions (venue_id, role, permission)
SELECT v.id, 'cashier', p.perm
FROM venues v
CROSS JOIN (VALUES
  ('stations.view'), ('stations.control'),
  ('sessions.start'), ('sessions.end'), ('sessions.refund'),
  ('members.view'), ('members.create'), ('members.ban'), ('members.topup'),
  ('bookings.view'), ('bookings.create'), ('bookings.cancel'),
  ('pos.orders'), ('pos.catalog'),
  ('analytics.view'), ('analytics.export'),
  ('staff.manage'),
  ('settings.edit')
) AS p(perm)
WHERE v.deleted_at IS NULL
ON CONFLICT (venue_id, role, permission) DO NOTHING;

-- Staff gets: view + sessions + bookings + pos.orders
INSERT INTO staff_permissions (venue_id, role, permission)
SELECT v.id, 'staff', p.perm
FROM venues v
CROSS JOIN (VALUES
  ('stations.view'),
  ('sessions.start'), ('sessions.end'),
  ('members.view'),
  ('bookings.view'), ('bookings.create'),
  ('pos.orders')
) AS p(perm)
WHERE v.deleted_at IS NULL
ON CONFLICT (venue_id, role, permission) DO NOTHING;

-- Technician gets: stations + sessions + bookings view
INSERT INTO staff_permissions (venue_id, role, permission)
SELECT v.id, 'technician', p.perm
FROM venues v
CROSS JOIN (VALUES
  ('stations.view'), ('stations.control'),
  ('sessions.start'), ('sessions.end'),
  ('bookings.view')
) AS p(perm)
WHERE v.deleted_at IS NULL
ON CONFLICT (venue_id, role, permission) DO NOTHING;
