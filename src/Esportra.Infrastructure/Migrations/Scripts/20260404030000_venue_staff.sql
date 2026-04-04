-- Venue staff roles and invitations
-- Migration: 20260404030000_venue_staff.sql

CREATE TABLE IF NOT EXISTS venue_staff (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  venue_id    UUID NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
  user_id     UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
  role        TEXT NOT NULL CHECK (role IN ('owner', 'manager', 'cashier')),
  invited_by  UUID REFERENCES auth.users(id),
  invited_at  TIMESTAMPTZ DEFAULT NOW(),
  accepted_at TIMESTAMPTZ,
  UNIQUE(venue_id, user_id)
);

ALTER TABLE venue_staff ENABLE ROW LEVEL SECURITY;

CREATE INDEX IF NOT EXISTS idx_venue_staff_venue ON venue_staff(venue_id);
CREATE INDEX IF NOT EXISTS idx_venue_staff_user ON venue_staff(user_id);

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'venue_staff_service_all' AND tablename = 'venue_staff') THEN
    CREATE POLICY venue_staff_service_all ON venue_staff
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'venue_staff_own_select' AND tablename = 'venue_staff') THEN
    CREATE POLICY venue_staff_own_select ON venue_staff
      FOR SELECT USING (auth.uid() = user_id);
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'venue_staff_owner_all' AND tablename = 'venue_staff') THEN
    CREATE POLICY venue_staff_owner_all ON venue_staff
      FOR ALL USING (
        EXISTS (
          SELECT 1 FROM venue_staff vs WHERE vs.venue_id = venue_staff.venue_id AND vs.user_id = auth.uid() AND vs.role = 'owner'
        )
      );
  END IF;
END $$;

CREATE TABLE IF NOT EXISTS venue_staff_invites (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  venue_id    UUID NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
  email       TEXT NOT NULL,
  role        TEXT NOT NULL CHECK (role IN ('manager', 'cashier')),
  token       TEXT UNIQUE NOT NULL DEFAULT gen_random_uuid()::text,
  expires_at  TIMESTAMPTZ NOT NULL DEFAULT NOW() + INTERVAL '7 days',
  used_at     TIMESTAMPTZ,
  invited_by  UUID REFERENCES auth.users(id)
);

ALTER TABLE venue_staff_invites ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'venue_staff_invites_service_all' AND tablename = 'venue_staff_invites') THEN
    CREATE POLICY venue_staff_invites_service_all ON venue_staff_invites
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;
