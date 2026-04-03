-- Real-time venue availability snapshot
-- Migration: 20260404090000_venue_availability_snapshot.sql

CREATE TABLE IF NOT EXISTS venue_availability_snapshot (
  venue_id        UUID PRIMARY KEY REFERENCES venues(id) ON DELETE CASCADE,
  total_stations  INT NOT NULL DEFAULT 0,
  available       INT NOT NULL DEFAULT 0,
  occupied        INT NOT NULL DEFAULT 0,
  maintenance     INT NOT NULL DEFAULT 0,
  offline         INT NOT NULL DEFAULT 0,
  updated_at      TIMESTAMPTZ DEFAULT NOW()
);

ALTER TABLE venue_availability_snapshot ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'venue_avail_public_read' AND tablename = 'venue_availability_snapshot') THEN
    CREATE POLICY venue_avail_public_read ON venue_availability_snapshot
      FOR SELECT USING (true);
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'venue_avail_service_all' AND tablename = 'venue_availability_snapshot') THEN
    CREATE POLICY venue_avail_service_all ON venue_availability_snapshot
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;
