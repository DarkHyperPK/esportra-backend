-- Station maintenance mode support
-- Migration: 20260404050000_station_maintenance.sql

ALTER TABLE venue_stations
  ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'active'
    CHECK (status IN ('active', 'maintenance', 'decommissioned')),
  ADD COLUMN IF NOT EXISTS maintenance_note TEXT,
  ADD COLUMN IF NOT EXISTS maintenance_since TIMESTAMPTZ;
