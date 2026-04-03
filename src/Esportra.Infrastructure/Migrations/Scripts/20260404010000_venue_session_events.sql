-- ============================================================
-- Venue Session Events
-- Tracks lifecycle events for venue sessions (started, ended, extended, locked, alert).
-- Used by the hub/backend to audit session state changes.
-- ============================================================

CREATE TABLE IF NOT EXISTS venue_session_events (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id  UUID REFERENCES venue_sessions(id) ON DELETE CASCADE,
  event_type  TEXT NOT NULL CHECK (event_type IN ('started','ended','extended','locked','alert')),
  metadata    JSONB DEFAULT '{}',
  created_at  TIMESTAMPTZ DEFAULT NOW()
);

-- Fast lookup: all events for a session, newest first
CREATE INDEX IF NOT EXISTS idx_venue_session_events_session
  ON venue_session_events(session_id, created_at DESC);

-- RLS: deny by default
ALTER TABLE venue_session_events ENABLE ROW LEVEL SECURITY;

-- Service role (hub / backend) full access
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_session_events_service_all'
      AND tablename  = 'venue_session_events'
  ) THEN
    CREATE POLICY venue_session_events_service_all
      ON venue_session_events
      FOR ALL
      USING (auth.role() = 'service_role')
      WITH CHECK (auth.role() = 'service_role');
  END IF;
END $$;
