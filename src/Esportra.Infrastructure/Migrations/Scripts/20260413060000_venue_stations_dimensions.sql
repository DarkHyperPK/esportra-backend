-- Add width, height, rotation columns to venue_stations for floor map rendering
-- Migration: 20260413060000_venue_stations_dimensions.sql
-- Note: Must run as supabase_admin (table owner). Applied manually on staging.
-- The ADD COLUMN IF NOT EXISTS makes this idempotent for future environments.

ALTER TABLE venue_stations
  ADD COLUMN IF NOT EXISTS width    NUMERIC(5,2) NOT NULL DEFAULT 1,
  ADD COLUMN IF NOT EXISTS height   NUMERIC(5,2) NOT NULL DEFAULT 1,
  ADD COLUMN IF NOT EXISTS rotation NUMERIC(5,2) NOT NULL DEFAULT 0;
