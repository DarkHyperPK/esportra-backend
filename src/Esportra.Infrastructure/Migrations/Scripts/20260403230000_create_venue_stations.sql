-- Phase 2C: Venue stations table for floor map positioning
CREATE TABLE IF NOT EXISTS venue_stations (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  venue_id    UUID NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
  station_id  TEXT NOT NULL,
  label       TEXT NOT NULL DEFAULT '',
  zone        TEXT,
  pos_x       NUMERIC(5,2),
  pos_y       NUMERIC(5,2),
  created_at  TIMESTAMPTZ DEFAULT NOW(),
  UNIQUE(venue_id, station_id)
);

ALTER TABLE venue_stations ENABLE ROW LEVEL SECURITY;

-- Service role (hub) can do everything
DO $$ BEGIN
IF NOT EXISTS (
  SELECT 1 FROM pg_policies WHERE tablename = 'venue_stations' AND policyname = 'venue_stations_service_role_all'
) THEN
  CREATE POLICY venue_stations_service_role_all ON venue_stations
    FOR ALL
    USING (auth.role() = 'service_role')
    WITH CHECK (auth.role() = 'service_role');
END IF;
END $$;

-- Venue owners can read their own station positions
DO $$ BEGIN
IF NOT EXISTS (
  SELECT 1 FROM pg_policies WHERE tablename = 'venue_stations' AND policyname = 'venue_stations_owner_select'
) THEN
  CREATE POLICY venue_stations_owner_select ON venue_stations
    FOR SELECT
    USING (
      EXISTS (
        SELECT 1 FROM venues v WHERE v.id = venue_stations.venue_id AND v.owner_id = auth.uid()
      )
    );
END IF;
END $$;
