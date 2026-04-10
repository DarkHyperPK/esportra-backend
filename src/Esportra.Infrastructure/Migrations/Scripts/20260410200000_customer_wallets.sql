-- ============================================================================
-- Migration: 20260410200000_customer_wallets.sql
-- Purpose:   Prepaid wallet system — one wallet per user per venue,
--            with a full transaction audit trail.
-- ============================================================================

-- ── Tables ──────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS customer_wallets (
    id          UUID          PRIMARY KEY DEFAULT uuid_generate_v4(),
    user_id     UUID          NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    venue_id    UUID          NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
    balance     NUMERIC(12,2) NOT NULL DEFAULT 0.00,
    currency    TEXT          NOT NULL DEFAULT 'USD',
    is_active   BOOLEAN       NOT NULL DEFAULT true,
    created_at  TIMESTAMPTZ   NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ   NOT NULL DEFAULT now(),
    UNIQUE(user_id, venue_id)
);

CREATE TABLE IF NOT EXISTS wallet_transactions (
    id            UUID          PRIMARY KEY DEFAULT uuid_generate_v4(),
    wallet_id     UUID          NOT NULL REFERENCES customer_wallets(id) ON DELETE CASCADE,
    venue_id      UUID          NOT NULL REFERENCES venues(id),
    type          TEXT          NOT NULL CHECK (type IN ('topup', 'deduct', 'bonus', 'refund', 'redeem')),
    amount        NUMERIC(12,2) NOT NULL,
    balance_after NUMERIC(12,2) NOT NULL,
    description   TEXT,
    reference_id  TEXT,
    created_by    UUID          REFERENCES auth.users(id),
    created_at    TIMESTAMPTZ   NOT NULL DEFAULT now()
);

-- ── Indexes ─────────────────────────────────────────────────────────────────

CREATE INDEX IF NOT EXISTS idx_customer_wallets_user   ON customer_wallets(user_id);
CREATE INDEX IF NOT EXISTS idx_customer_wallets_venue  ON customer_wallets(venue_id);

CREATE INDEX IF NOT EXISTS idx_wallet_txn_wallet       ON wallet_transactions(wallet_id);
CREATE INDEX IF NOT EXISTS idx_wallet_txn_venue        ON wallet_transactions(venue_id);
CREATE INDEX IF NOT EXISTS idx_wallet_txn_created_by   ON wallet_transactions(created_by);
CREATE INDEX IF NOT EXISTS idx_wallet_txn_created_desc ON wallet_transactions(created_at DESC);

-- ── Auto-update timestamp trigger ───────────────────────────────────────────

CREATE OR REPLACE FUNCTION update_customer_wallets_updated_at()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_customer_wallets_updated_at ON customer_wallets;
CREATE TRIGGER trg_customer_wallets_updated_at
    BEFORE UPDATE ON customer_wallets
    FOR EACH ROW EXECUTE FUNCTION update_customer_wallets_updated_at();

-- ── Row Level Security ──────────────────────────────────────────────────────

ALTER TABLE customer_wallets   ENABLE ROW LEVEL SECURITY;
ALTER TABLE wallet_transactions ENABLE ROW LEVEL SECURITY;

-- ── customer_wallets policies ───────────────────────────────────────────────

-- Service role: full access
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'customer_wallets_service_all' AND tablename = 'customer_wallets'
  ) THEN
    CREATE POLICY customer_wallets_service_all ON customer_wallets
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;

-- Users can SELECT their own wallets
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'customer_wallets_own_select' AND tablename = 'customer_wallets'
  ) THEN
    CREATE POLICY customer_wallets_own_select ON customer_wallets
      FOR SELECT USING (auth.uid() = user_id);
  END IF;
END $$;

-- Venue staff (owner/manager/cashier) can SELECT wallets for their venue
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'customer_wallets_staff_select' AND tablename = 'customer_wallets'
  ) THEN
    CREATE POLICY customer_wallets_staff_select ON customer_wallets
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = customer_wallets.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

-- Venue staff can INSERT wallets for their venue
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'customer_wallets_staff_insert' AND tablename = 'customer_wallets'
  ) THEN
    CREATE POLICY customer_wallets_staff_insert ON customer_wallets
      FOR INSERT WITH CHECK (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = customer_wallets.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

-- Venue staff can UPDATE wallets for their venue
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'customer_wallets_staff_update' AND tablename = 'customer_wallets'
  ) THEN
    CREATE POLICY customer_wallets_staff_update ON customer_wallets
      FOR UPDATE USING (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = customer_wallets.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      ) WITH CHECK (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = customer_wallets.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

-- ── wallet_transactions policies ────────────────────────────────────────────

-- Service role: full access
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'wallet_txn_service_all' AND tablename = 'wallet_transactions'
  ) THEN
    CREATE POLICY wallet_txn_service_all ON wallet_transactions
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;

-- Users can SELECT their own transactions (via wallet ownership)
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'wallet_txn_own_select' AND tablename = 'wallet_transactions'
  ) THEN
    CREATE POLICY wallet_txn_own_select ON wallet_transactions
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM customer_wallets cw
          WHERE cw.id      = wallet_transactions.wallet_id
            AND cw.user_id = auth.uid()
        )
      );
  END IF;
END $$;

-- Venue staff can SELECT transactions for their venue
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'wallet_txn_staff_select' AND tablename = 'wallet_transactions'
  ) THEN
    CREATE POLICY wallet_txn_staff_select ON wallet_transactions
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = wallet_transactions.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

-- Venue staff can INSERT transactions for their venue
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'wallet_txn_staff_insert' AND tablename = 'wallet_transactions'
  ) THEN
    CREATE POLICY wallet_txn_staff_insert ON wallet_transactions
      FOR INSERT WITH CHECK (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = wallet_transactions.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

-- ── Grants ──────────────────────────────────────────────────────────────────

GRANT ALL ON customer_wallets TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON customer_wallets TO authenticated;

GRANT ALL ON wallet_transactions TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON wallet_transactions TO authenticated;
