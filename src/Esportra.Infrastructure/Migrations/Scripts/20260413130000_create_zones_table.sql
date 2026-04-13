-- Phase 3.1: Proper zones table (replaces JSONB zones in venue_billing_config)
-- Zones define station groupings with independent hourly rates.

CREATE TABLE IF NOT EXISTS zones (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  venue_id    UUID NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
  name        TEXT NOT NULL,
  color       TEXT NOT NULL DEFAULT '#3b82f6',
  hourly_rate NUMERIC(10,2) NOT NULL DEFAULT 0,
  sort_order  INT NOT NULL DEFAULT 0,
  is_active   BOOLEAN NOT NULL DEFAULT true,
  created_at  TIMESTAMPTZ DEFAULT NOW(),
  updated_at  TIMESTAMPTZ DEFAULT NOW(),
  UNIQUE(venue_id, name)
);

CREATE INDEX IF NOT EXISTS idx_zones_venue ON zones(venue_id, sort_order);

ALTER TABLE zones ENABLE ROW LEVEL SECURITY;

-- Service role full access
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'zones_service_all' AND tablename = 'zones') THEN
    CREATE POLICY zones_service_all ON zones
      FOR ALL USING (current_setting('request.jwt.claim.role', true) = 'service_role');
  END IF;
END $$;

-- Venue owner/staff can read zones
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'zones_owner_select' AND tablename = 'zones') THEN
    CREATE POLICY zones_owner_select ON zones
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venues WHERE venues.id = zones.venue_id AND venues.owner_id = auth.uid()
        )
        OR EXISTS (
          SELECT 1 FROM venue_staff WHERE venue_staff.venue_id = zones.venue_id AND venue_staff.user_id = auth.uid() AND venue_staff.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

-- GRANTs
GRANT ALL ON zones TO service_role;
GRANT SELECT ON zones TO authenticated;
