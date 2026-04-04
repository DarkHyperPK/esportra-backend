-- ============================================================
-- Station Health Snapshots
-- Periodic hardware telemetry from venue gaming stations.
-- Written by the desktop hub agent, read by venue dashboards.
-- ============================================================

CREATE TABLE IF NOT EXISTS station_health_snapshots (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  venue_id    UUID NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
  station_id  TEXT NOT NULL,
  cpu_temp    NUMERIC(5,2),
  gpu_temp    NUMERIC(5,2),
  ram_pct     NUMERIC(5,2),
  disk_pct    NUMERIC(5,2),
  latency_ms  INT,
  recorded_at TIMESTAMPTZ DEFAULT NOW()
);

-- Fast lookup: latest snapshot per station
CREATE INDEX IF NOT EXISTS idx_health_snapshots_station
  ON station_health_snapshots(station_id, recorded_at DESC);

-- Fast lookup: all snapshots for a venue, newest first
CREATE INDEX IF NOT EXISTS idx_health_snapshots_venue
  ON station_health_snapshots(venue_id, recorded_at DESC);

-- RLS: deny by default
ALTER TABLE station_health_snapshots ENABLE ROW LEVEL SECURITY;

-- Service role (hub / backend) full access
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'health_snapshots_service_all'
      AND tablename  = 'station_health_snapshots'
  ) THEN
    CREATE POLICY health_snapshots_service_all
      ON station_health_snapshots
      FOR ALL
      USING (auth.role() = 'service_role')
      WITH CHECK (auth.role() = 'service_role');
  END IF;
END $$;
