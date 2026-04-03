-- Phase 2A.2: Create venue_sessions table
-- Tracks active and historical station sessions (web bookings, walk-ins, admin sessions)

CREATE TABLE IF NOT EXISTS venue_sessions (
  id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  venue_id         UUID NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
  station_id       TEXT NOT NULL,
  user_id          UUID REFERENCES auth.users(id),
  booking_id       UUID REFERENCES venue_bookings(id),
  session_type     TEXT NOT NULL DEFAULT 'web_booking'
    CHECK (session_type IN ('web_booking', 'hourly', 'package', 'voucher', 'admin', 'complimentary')),
  started_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  expires_at       TIMESTAMPTZ NOT NULL,
  ended_at         TIMESTAMPTZ,
  total_charged    NUMERIC(10,2) DEFAULT 0,
  payment_method   TEXT,
  display_name     TEXT,
  created_at       TIMESTAMPTZ DEFAULT NOW()
);

ALTER TABLE venue_sessions ENABLE ROW LEVEL SECURITY;

-- Active session lookup by station
CREATE INDEX IF NOT EXISTS idx_venue_sessions_active
  ON venue_sessions(station_id)
  WHERE ended_at IS NULL;

-- Venue session history
CREATE INDEX IF NOT EXISTS idx_venue_sessions_venue
  ON venue_sessions(venue_id, started_at DESC);

-- RLS: Service role full access (hub uses service key)
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'venue_sessions_service_all' AND tablename = 'venue_sessions') THEN
    CREATE POLICY venue_sessions_service_all ON venue_sessions
      FOR ALL USING (current_setting('request.jwt.claim.role', true) = 'service_role');
  END IF;
END $$;

-- RLS: Venue owners can read their venue's sessions
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'venue_sessions_owner_select' AND tablename = 'venue_sessions') THEN
    CREATE POLICY venue_sessions_owner_select ON venue_sessions
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venues WHERE venues.id = venue_sessions.venue_id AND venues.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- RLS: Users can view their own sessions
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'venue_sessions_user_select' AND tablename = 'venue_sessions') THEN
    CREATE POLICY venue_sessions_user_select ON venue_sessions
      FOR SELECT USING (auth.uid() = user_id);
  END IF;
END $$;
