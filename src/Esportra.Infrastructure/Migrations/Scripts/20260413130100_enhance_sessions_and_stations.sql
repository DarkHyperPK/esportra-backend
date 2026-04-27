-- Phase 3.2 + 3.4: Add zone_id FK to stations, add billing columns to sessions

-- Link stations to the new zones table
ALTER TABLE venue_stations
  ADD COLUMN IF NOT EXISTS zone_id UUID REFERENCES zones(id) ON DELETE SET NULL;

CREATE INDEX IF NOT EXISTS idx_venue_stations_zone ON venue_stations(zone_id);

-- Enhance venue_sessions with billing/lifecycle columns
ALTER TABLE venue_sessions
  ADD COLUMN IF NOT EXISTS rate_per_hour  NUMERIC(10,2),
  ADD COLUMN IF NOT EXISTS member_id      UUID,
  ADD COLUMN IF NOT EXISTS ended_by       UUID,
  ADD COLUMN IF NOT EXISTS duration_minutes INT;

-- Add more event types to session events
ALTER TABLE venue_session_events
  DROP CONSTRAINT IF EXISTS venue_session_events_event_type_check;

ALTER TABLE venue_session_events
  ADD CONSTRAINT venue_session_events_event_type_check
    CHECK (event_type IN ('started','ended','extended','locked','unlocked','alert','paused','resumed','billed'));

-- Index for member session lookups
CREATE INDEX IF NOT EXISTS idx_venue_sessions_member ON venue_sessions(member_id) WHERE member_id IS NOT NULL;
