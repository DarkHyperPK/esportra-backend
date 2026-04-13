-- Booking rules, venue_bookings enhancements, and walk-in queue
-- Adds configurable booking rules per venue, enriches venue_bookings with
-- source/zone/member/session/check-in tracking, and introduces a walk-in queue.

-- ============================================================================
-- 1. booking_rules — one row per venue, configurable booking constraints
-- ============================================================================

CREATE TABLE IF NOT EXISTS booking_rules (
  id                       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  venue_id                 UUID NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
  min_duration_minutes     INT NOT NULL DEFAULT 30,
  max_duration_minutes     INT NOT NULL DEFAULT 480,
  advance_booking_days     INT NOT NULL DEFAULT 14,
  cancellation_minutes     INT NOT NULL DEFAULT 60,
  no_show_cancel_minutes   INT NOT NULL DEFAULT 15,
  buffer_minutes           INT NOT NULL DEFAULT 5,
  max_stations_per_booking INT NOT NULL DEFAULT 1,
  allow_walk_ins           BOOLEAN NOT NULL DEFAULT true,
  require_payment_upfront  BOOLEAN NOT NULL DEFAULT false,
  peak_hours               JSONB DEFAULT '[]'::jsonb,
  off_peak_discount_percent NUMERIC(5,2) DEFAULT 0,
  created_at               TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at               TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE(venue_id)
);

CREATE INDEX IF NOT EXISTS idx_booking_rules_venue ON booking_rules(venue_id);

ALTER TABLE booking_rules ENABLE ROW LEVEL SECURITY;

-- ============================================================================
-- 2. venue_bookings — add source, zone, member, session, check-in columns
-- ============================================================================

ALTER TABLE venue_bookings ADD COLUMN IF NOT EXISTS source TEXT DEFAULT 'web';
ALTER TABLE venue_bookings ADD COLUMN IF NOT EXISTS zone_id UUID REFERENCES zones(id);
ALTER TABLE venue_bookings ADD COLUMN IF NOT EXISTS member_id UUID REFERENCES members(id);
ALTER TABLE venue_bookings ADD COLUMN IF NOT EXISTS session_id UUID REFERENCES venue_sessions(id);
ALTER TABLE venue_bookings ADD COLUMN IF NOT EXISTS checked_in_at TIMESTAMPTZ;
ALTER TABLE venue_bookings ADD COLUMN IF NOT EXISTS no_show_at TIMESTAMPTZ;

-- ============================================================================
-- 3. walk_in_queue — queued walk-in customers waiting for a station
-- ============================================================================

CREATE TABLE IF NOT EXISTS walk_in_queue (
  id                        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  venue_id                  UUID NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
  member_id                 UUID REFERENCES members(id),
  customer_name             TEXT,
  zone_id                   UUID REFERENCES zones(id),
  station_preference        TEXT,
  requested_duration_minutes INT DEFAULT 60,
  status                    TEXT NOT NULL DEFAULT 'waiting'
    CHECK (status IN ('waiting', 'assigned', 'cancelled', 'expired')),
  assigned_station_id       TEXT,
  assigned_at               TIMESTAMPTZ,
  created_at                TIMESTAMPTZ NOT NULL DEFAULT now(),
  position                  INT NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_walk_in_queue_venue_status
  ON walk_in_queue(venue_id, status);

CREATE INDEX IF NOT EXISTS idx_walk_in_queue_venue_position
  ON walk_in_queue(venue_id, position)
  WHERE status = 'waiting';

ALTER TABLE walk_in_queue ENABLE ROW LEVEL SECURITY;

-- ============================================================================
-- 4. RLS policies — booking_rules
-- ============================================================================

-- Service role: full access
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'booking_rules_service_all' AND tablename = 'booking_rules') THEN
    CREATE POLICY booking_rules_service_all ON booking_rules
      FOR ALL USING (current_setting('request.jwt.claim.role', true) = 'service_role');
  END IF;
END $$;

-- Venue owner can read their own rules
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'booking_rules_owner_select' AND tablename = 'booking_rules') THEN
    CREATE POLICY booking_rules_owner_select ON booking_rules
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venues
          WHERE venues.id = booking_rules.venue_id AND venues.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- Venue staff can read their venue's rules
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'booking_rules_staff_select' AND tablename = 'booking_rules') THEN
    CREATE POLICY booking_rules_staff_select ON booking_rules
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venue_staff
          WHERE venue_staff.venue_id = booking_rules.venue_id
            AND venue_staff.user_id = auth.uid()
            AND venue_staff.status = 'active'
        )
      );
  END IF;
END $$;

-- ============================================================================
-- 5. RLS policies — walk_in_queue
-- ============================================================================

-- Service role: full access
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'walk_in_queue_service_all' AND tablename = 'walk_in_queue') THEN
    CREATE POLICY walk_in_queue_service_all ON walk_in_queue
      FOR ALL USING (current_setting('request.jwt.claim.role', true) = 'service_role');
  END IF;
END $$;

-- Venue owner can read their queue
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'walk_in_queue_owner_select' AND tablename = 'walk_in_queue') THEN
    CREATE POLICY walk_in_queue_owner_select ON walk_in_queue
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venues
          WHERE venues.id = walk_in_queue.venue_id AND venues.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- Venue staff can read their queue
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'walk_in_queue_staff_select' AND tablename = 'walk_in_queue') THEN
    CREATE POLICY walk_in_queue_staff_select ON walk_in_queue
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venue_staff
          WHERE venue_staff.venue_id = walk_in_queue.venue_id
            AND venue_staff.user_id = auth.uid()
            AND venue_staff.status = 'active'
        )
      );
  END IF;
END $$;

-- ============================================================================
-- 6. GRANTs
-- ============================================================================

GRANT ALL ON booking_rules TO service_role;
GRANT SELECT ON booking_rules TO authenticated;

GRANT ALL ON walk_in_queue TO service_role;
GRANT SELECT ON walk_in_queue TO authenticated;
