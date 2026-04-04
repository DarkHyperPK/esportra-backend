-- Phase 2B: Add notes column to venue_sessions + prevent duplicate active sessions
ALTER TABLE venue_sessions
  ADD COLUMN IF NOT EXISTS notes TEXT;

-- Prevent two active sessions on the same station at the same venue
CREATE UNIQUE INDEX IF NOT EXISTS idx_venue_sessions_one_active_per_station
  ON venue_sessions(station_id, venue_id)
  WHERE ended_at IS NULL;
