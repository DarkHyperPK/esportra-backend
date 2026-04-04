-- Receipt generation for walk-in sessions
-- Migration: 20260404070000_venue_receipts.sql

CREATE TABLE IF NOT EXISTS venue_receipts (
  id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id      UUID REFERENCES venue_sessions(id),
  venue_id        UUID NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
  receipt_no      TEXT NOT NULL,
  items           JSONB NOT NULL DEFAULT '[]',
  subtotal        NUMERIC(10,2),
  tax             NUMERIC(10,2),
  total           NUMERIC(10,2),
  payment_method  TEXT,
  issued_at       TIMESTAMPTZ DEFAULT NOW(),
  issued_by       UUID REFERENCES auth.users(id)
);

CREATE INDEX IF NOT EXISTS idx_venue_receipts_venue ON venue_receipts(venue_id, issued_at DESC);
CREATE INDEX IF NOT EXISTS idx_venue_receipts_session ON venue_receipts(session_id);

ALTER TABLE venue_receipts ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'venue_receipts_service_all' AND tablename = 'venue_receipts') THEN
    CREATE POLICY venue_receipts_service_all ON venue_receipts
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'venue_receipts_owner_select' AND tablename = 'venue_receipts') THEN
    CREATE POLICY venue_receipts_owner_select ON venue_receipts
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venues WHERE venues.id = venue_receipts.venue_id AND venues.owner_id = auth.uid()
        )
      );
  END IF;
END $$;
