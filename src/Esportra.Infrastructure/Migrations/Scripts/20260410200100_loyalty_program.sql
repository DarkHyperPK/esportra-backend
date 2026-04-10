-- ============================================================================
-- Migration: 20260410200100_loyalty_program.sql
-- Purpose:   Hybrid loyalty system — global accounts with per-venue earning,
--            configurable tiers, and a redeemable rewards catalogue.
-- ============================================================================

-- ── Tables ──────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS loyalty_accounts (
    id              UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
    user_id         UUID        NOT NULL UNIQUE REFERENCES auth.users(id) ON DELETE CASCADE,
    total_points    INTEGER     NOT NULL DEFAULT 0,
    lifetime_points INTEGER     NOT NULL DEFAULT 0,
    current_tier    TEXT        NOT NULL DEFAULT 'bronze'
                                CHECK (current_tier IN ('bronze', 'silver', 'gold', 'platinum')),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS loyalty_transactions (
    id          UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
    account_id  UUID        NOT NULL REFERENCES loyalty_accounts(id) ON DELETE CASCADE,
    venue_id    UUID        NOT NULL REFERENCES venues(id),
    type        TEXT        NOT NULL CHECK (type IN ('earn', 'redeem', 'bonus', 'expire', 'adjust')),
    points      INTEGER     NOT NULL,
    description TEXT,
    session_id  UUID,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS venue_loyalty_config (
    id                  UUID          PRIMARY KEY DEFAULT uuid_generate_v4(),
    venue_id            UUID          NOT NULL UNIQUE REFERENCES venues(id) ON DELETE CASCADE,
    points_per_hour     INTEGER       NOT NULL DEFAULT 10,
    bonus_multiplier    NUMERIC(3,1)  NOT NULL DEFAULT 1.0,
    min_session_minutes INTEGER       NOT NULL DEFAULT 30,
    tiers               JSONB         NOT NULL DEFAULT '[
        {"name":"bronze","min_points":0,"multiplier":1.0,"perks":"Standard rates"},
        {"name":"silver","min_points":500,"multiplier":1.2,"perks":"5% discount on sessions"},
        {"name":"gold","min_points":2000,"multiplier":1.5,"perks":"10% discount + priority booking"},
        {"name":"platinum","min_points":5000,"multiplier":2.0,"perks":"15% discount + free hour monthly"}
    ]'::jsonb,
    rewards             JSONB         NOT NULL DEFAULT '[
        {"id":"free_30min","name":"Free 30 Minutes","points_cost":100,"type":"time","value":30},
        {"id":"free_1hr","name":"Free 1 Hour","points_cost":180,"type":"time","value":60},
        {"id":"wallet_5","name":"$5 Wallet Credit","points_cost":200,"type":"wallet_credit","value":5}
    ]'::jsonb,
    is_active           BOOLEAN       NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ   NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ   NOT NULL DEFAULT now()
);

-- ── Indexes ─────────────────────────────────────────────────────────────────

CREATE INDEX IF NOT EXISTS idx_loyalty_accounts_user         ON loyalty_accounts(user_id);

CREATE INDEX IF NOT EXISTS idx_loyalty_txn_account           ON loyalty_transactions(account_id);
CREATE INDEX IF NOT EXISTS idx_loyalty_txn_venue             ON loyalty_transactions(venue_id);
CREATE INDEX IF NOT EXISTS idx_loyalty_txn_created_desc      ON loyalty_transactions(created_at DESC);

CREATE INDEX IF NOT EXISTS idx_venue_loyalty_config_venue    ON venue_loyalty_config(venue_id);

-- ── Auto-update timestamp triggers ──────────────────────────────────────────

CREATE OR REPLACE FUNCTION update_loyalty_accounts_updated_at()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_loyalty_accounts_updated_at ON loyalty_accounts;
CREATE TRIGGER trg_loyalty_accounts_updated_at
    BEFORE UPDATE ON loyalty_accounts
    FOR EACH ROW EXECUTE FUNCTION update_loyalty_accounts_updated_at();

CREATE OR REPLACE FUNCTION update_venue_loyalty_config_updated_at()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_venue_loyalty_config_updated_at ON venue_loyalty_config;
CREATE TRIGGER trg_venue_loyalty_config_updated_at
    BEFORE UPDATE ON venue_loyalty_config
    FOR EACH ROW EXECUTE FUNCTION update_venue_loyalty_config_updated_at();

-- ── Row Level Security ──────────────────────────────────────────────────────

ALTER TABLE loyalty_accounts     ENABLE ROW LEVEL SECURITY;
ALTER TABLE loyalty_transactions ENABLE ROW LEVEL SECURITY;
ALTER TABLE venue_loyalty_config ENABLE ROW LEVEL SECURITY;

-- ── loyalty_accounts policies ───────────────────────────────────────────────

-- Service role: full access (system manages points & tiers)
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'loyalty_accounts_service_all' AND tablename = 'loyalty_accounts'
  ) THEN
    CREATE POLICY loyalty_accounts_service_all ON loyalty_accounts
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;

-- Users can SELECT their own loyalty account
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'loyalty_accounts_own_select' AND tablename = 'loyalty_accounts'
  ) THEN
    CREATE POLICY loyalty_accounts_own_select ON loyalty_accounts
      FOR SELECT USING (auth.uid() = user_id);
  END IF;
END $$;

-- Venue staff can SELECT loyalty accounts for users who have transacted at their venue
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'loyalty_accounts_staff_select' AND tablename = 'loyalty_accounts'
  ) THEN
    CREATE POLICY loyalty_accounts_staff_select ON loyalty_accounts
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM loyalty_transactions lt
          JOIN venue_staff vs ON vs.venue_id = lt.venue_id
          WHERE lt.account_id      = loyalty_accounts.id
            AND vs.user_id         = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

-- ── loyalty_transactions policies ───────────────────────────────────────────

-- Service role: full access
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'loyalty_txn_service_all' AND tablename = 'loyalty_transactions'
  ) THEN
    CREATE POLICY loyalty_txn_service_all ON loyalty_transactions
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;

-- Users can SELECT their own transactions (via account ownership)
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'loyalty_txn_own_select' AND tablename = 'loyalty_transactions'
  ) THEN
    CREATE POLICY loyalty_txn_own_select ON loyalty_transactions
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM loyalty_accounts la
          WHERE la.id      = loyalty_transactions.account_id
            AND la.user_id = auth.uid()
        )
      );
  END IF;
END $$;

-- Venue staff can SELECT transactions at their venue
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'loyalty_txn_staff_select' AND tablename = 'loyalty_transactions'
  ) THEN
    CREATE POLICY loyalty_txn_staff_select ON loyalty_transactions
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = loyalty_transactions.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

-- ── venue_loyalty_config policies ───────────────────────────────────────────

-- Service role: full access
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_loyalty_config_service_all' AND tablename = 'venue_loyalty_config'
  ) THEN
    CREATE POLICY venue_loyalty_config_service_all ON venue_loyalty_config
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;

-- Venue owners can SELECT their config
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_loyalty_config_owner_select' AND tablename = 'venue_loyalty_config'
  ) THEN
    CREATE POLICY venue_loyalty_config_owner_select ON venue_loyalty_config
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venues v
          WHERE v.id       = venue_loyalty_config.venue_id
            AND v.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- Venue owners can UPDATE their config
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_loyalty_config_owner_update' AND tablename = 'venue_loyalty_config'
  ) THEN
    CREATE POLICY venue_loyalty_config_owner_update ON venue_loyalty_config
      FOR UPDATE USING (
        EXISTS (
          SELECT 1 FROM venues v
          WHERE v.id       = venue_loyalty_config.venue_id
            AND v.owner_id = auth.uid()
        )
      ) WITH CHECK (
        EXISTS (
          SELECT 1 FROM venues v
          WHERE v.id       = venue_loyalty_config.venue_id
            AND v.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- Venue staff can SELECT config for their venue
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_loyalty_config_staff_select' AND tablename = 'venue_loyalty_config'
  ) THEN
    CREATE POLICY venue_loyalty_config_staff_select ON venue_loyalty_config
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venue_staff vs
          WHERE vs.venue_id = venue_loyalty_config.venue_id
            AND vs.user_id  = auth.uid()
            AND vs.accepted_at IS NOT NULL
        )
      );
  END IF;
END $$;

-- Public read for active configs (gamers can see loyalty info)
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE policyname = 'venue_loyalty_config_public_select' AND tablename = 'venue_loyalty_config'
  ) THEN
    CREATE POLICY venue_loyalty_config_public_select ON venue_loyalty_config
      FOR SELECT USING (is_active = true);
  END IF;
END $$;

-- ── Grants ──────────────────────────────────────────────────────────────────

GRANT ALL ON loyalty_accounts TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON loyalty_accounts TO authenticated;

GRANT ALL ON loyalty_transactions TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON loyalty_transactions TO authenticated;

GRANT ALL ON venue_loyalty_config TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON venue_loyalty_config TO authenticated;
