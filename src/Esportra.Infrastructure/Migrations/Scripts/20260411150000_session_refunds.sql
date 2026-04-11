-- Session refund tracking
-- Migration: 20260411150000_session_refunds.sql

-- 1. Add refund columns to venue_sessions
ALTER TABLE venue_sessions ADD COLUMN IF NOT EXISTS refund_amount   NUMERIC(10,2) DEFAULT 0;
ALTER TABLE venue_sessions ADD COLUMN IF NOT EXISTS refund_method   TEXT CHECK (refund_method IN ('wallet', 'cash', NULL));
ALTER TABLE venue_sessions ADD COLUMN IF NOT EXISTS refund_reason   TEXT;
ALTER TABLE venue_sessions ADD COLUMN IF NOT EXISTS refunded_at     TIMESTAMPTZ;
ALTER TABLE venue_sessions ADD COLUMN IF NOT EXISTS refunded_by     UUID REFERENCES auth.users(id);

CREATE INDEX IF NOT EXISTS idx_venue_sessions_refunded ON venue_sessions(venue_id) WHERE refunded_at IS NOT NULL;

-- 2. Detailed refund audit log (supports multiple partial refunds per session)
CREATE TABLE IF NOT EXISTS session_refunds (
  id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  venue_id        UUID NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
  session_id      UUID NOT NULL REFERENCES venue_sessions(id) ON DELETE CASCADE,
  amount          NUMERIC(10,2) NOT NULL CHECK (amount > 0),
  method          TEXT NOT NULL CHECK (method IN ('wallet', 'cash')),
  reason          TEXT NOT NULL,
  wallet_txn_id   UUID REFERENCES wallet_transactions(id),
  refunded_by     UUID NOT NULL REFERENCES auth.users(id),
  created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE session_refunds ENABLE ROW LEVEL SECURITY;

CREATE INDEX IF NOT EXISTS idx_session_refunds_session ON session_refunds(session_id);
CREATE INDEX IF NOT EXISTS idx_session_refunds_venue   ON session_refunds(venue_id);

-- RLS: service_role full access
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'session_refunds_service_all' AND tablename = 'session_refunds') THEN
    CREATE POLICY session_refunds_service_all ON session_refunds
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;

-- RLS: venue staff can read refunds for their venue
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'session_refunds_staff_select' AND tablename = 'session_refunds') THEN
    CREATE POLICY session_refunds_staff_select ON session_refunds
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = session_refunds.venue_id
            AND vs.user_id = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

-- 3. GRANTs
GRANT ALL    ON session_refunds TO service_role;
GRANT SELECT ON session_refunds TO authenticated;

-- 4. Atomic refund-to-wallet function (prevents double-refund race conditions)
CREATE OR REPLACE FUNCTION process_session_refund(
  p_session_id   UUID,
  p_venue_id     UUID,
  p_amount       NUMERIC,
  p_method       TEXT,
  p_reason       TEXT,
  p_refunded_by  UUID
) RETURNS TABLE(refund_id UUID, wallet_txn_id UUID, total_refunded NUMERIC) AS $$
DECLARE
  v_session       RECORD;
  v_refund_id     UUID;
  v_wallet_txn_id UUID := NULL;
  v_total_refunded NUMERIC;
  v_wallet_id     UUID;
  v_new_balance   NUMERIC;
BEGIN
  -- 1. Lock session row to prevent concurrent refunds
  SELECT id, venue_id, total_charged, user_id,
         COALESCE(refund_amount, 0) AS already_refunded
  INTO v_session
  FROM venue_sessions
  WHERE id = p_session_id AND venue_id = p_venue_id
  FOR UPDATE;

  IF NOT FOUND THEN
    RAISE EXCEPTION 'Session not found';
  END IF;

  -- 2. Validate refund doesn't exceed charged amount
  v_total_refunded := v_session.already_refunded + p_amount;
  IF v_total_refunded > v_session.total_charged THEN
    RAISE EXCEPTION 'Refund amount (%) exceeds remaining refundable (% of % charged)',
      p_amount,
      v_session.total_charged - v_session.already_refunded,
      v_session.total_charged;
  END IF;

  -- 3. If wallet refund, credit the customer's wallet atomically
  IF p_method = 'wallet' THEN
    IF v_session.user_id IS NULL THEN
      RAISE EXCEPTION 'Cannot refund to wallet: session has no linked user';
    END IF;

    -- Find or create wallet
    INSERT INTO customer_wallets (user_id, venue_id, currency)
    SELECT v_session.user_id, p_venue_id,
           COALESCE((SELECT currency FROM venue_billing_config WHERE venue_id = p_venue_id LIMIT 1), 'GBP')
    ON CONFLICT (user_id, venue_id) DO NOTHING;

    SELECT id INTO v_wallet_id
    FROM customer_wallets
    WHERE user_id = v_session.user_id AND venue_id = p_venue_id;

    -- Credit wallet (locked row)
    UPDATE customer_wallets
    SET balance = balance + p_amount
    WHERE id = v_wallet_id
    RETURNING balance INTO v_new_balance;

    -- Record wallet transaction
    INSERT INTO wallet_transactions (wallet_id, type, amount, balance_after, description, reference_id)
    VALUES (v_wallet_id, 'refund', p_amount, v_new_balance,
            'Session refund: ' || p_reason, p_session_id::text)
    RETURNING id INTO v_wallet_txn_id;
  END IF;

  -- 4. Insert refund audit record
  INSERT INTO session_refunds (venue_id, session_id, amount, method, reason, wallet_txn_id, refunded_by)
  VALUES (p_venue_id, p_session_id, p_amount, p_method, p_reason, v_wallet_txn_id, p_refunded_by)
  RETURNING id INTO v_refund_id;

  -- 5. Update session with cumulative refund
  UPDATE venue_sessions
  SET refund_amount = v_total_refunded,
      refund_method = p_method,
      refund_reason = CASE WHEN refund_reason IS NULL THEN p_reason
                           ELSE refund_reason || '; ' || p_reason END,
      refunded_at   = NOW(),
      refunded_by   = p_refunded_by
  WHERE id = p_session_id;

  RETURN QUERY SELECT v_refund_id, v_wallet_txn_id, v_total_refunded;
END;
$$ LANGUAGE plpgsql SECURITY DEFINER SET search_path = public;
