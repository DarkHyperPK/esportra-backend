-- Phase 3.3: Session invoices — unified bill linking session charges + POS items

CREATE TABLE IF NOT EXISTS session_invoices (
  id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  venue_id         UUID NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
  session_id       UUID REFERENCES venue_sessions(id) ON DELETE SET NULL,
  member_id        UUID,
  station_id       TEXT,

  -- Line items stored as JSONB array: [{type, description, quantity, unit_price, total}]
  line_items       JSONB NOT NULL DEFAULT '[]',

  subtotal         NUMERIC(10,2) NOT NULL DEFAULT 0,
  tax              NUMERIC(10,2) NOT NULL DEFAULT 0,
  discount         NUMERIC(10,2) NOT NULL DEFAULT 0,
  total            NUMERIC(10,2) NOT NULL DEFAULT 0,

  payment_method   TEXT CHECK (payment_method IN ('cash', 'card', 'wallet', 'package', 'free', 'split')),
  status           TEXT NOT NULL DEFAULT 'pending'
    CHECK (status IN ('pending', 'paid', 'voided', 'refunded', 'partial_refund')),

  paid_at          TIMESTAMPTZ,
  created_by       UUID,
  notes            TEXT,
  created_at       TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_session_invoices_venue ON session_invoices(venue_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_session_invoices_session ON session_invoices(session_id) WHERE session_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_session_invoices_member ON session_invoices(member_id) WHERE member_id IS NOT NULL;

ALTER TABLE session_invoices ENABLE ROW LEVEL SECURITY;

-- Service role full access
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'session_invoices_service_all' AND tablename = 'session_invoices') THEN
    CREATE POLICY session_invoices_service_all ON session_invoices
      FOR ALL USING (current_setting('request.jwt.claim.role', true) = 'service_role');
  END IF;
END $$;

-- Venue owner/staff can read invoices
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'session_invoices_owner_select' AND tablename = 'session_invoices') THEN
    CREATE POLICY session_invoices_owner_select ON session_invoices
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venues WHERE venues.id = session_invoices.venue_id AND venues.owner_id = auth.uid()
        )
        OR EXISTS (
          SELECT 1 FROM venue_staff WHERE venue_staff.venue_id = session_invoices.venue_id AND venue_staff.user_id = auth.uid() AND venue_staff.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

-- GRANTs
GRANT ALL ON session_invoices TO service_role;
GRANT SELECT ON session_invoices TO authenticated;
