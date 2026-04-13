-- Add width, height, rotation columns to venue_stations for floor map rendering
-- Migration: 20260413060000_venue_stations_dimensions.sql

ALTER TABLE venue_stations
  ADD COLUMN IF NOT EXISTS width    NUMERIC(5,2) NOT NULL DEFAULT 1,
  ADD COLUMN IF NOT EXISTS height   NUMERIC(5,2) NOT NULL DEFAULT 1,
  ADD COLUMN IF NOT EXISTS rotation NUMERIC(5,2) NOT NULL DEFAULT 0;

COMMENT ON COLUMN venue_stations.width    IS 'Station tile width in grid units (default 1)';
COMMENT ON COLUMN venue_stations.height   IS 'Station tile height in grid units (default 1)';
COMMENT ON COLUMN venue_stations.rotation IS 'Station rotation in degrees (default 0)';
