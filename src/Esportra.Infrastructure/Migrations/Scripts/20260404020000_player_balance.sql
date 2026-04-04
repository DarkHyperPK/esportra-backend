-- Player balance accounts for venue session payments
-- Migration: 20260404020000_player_balance.sql

CREATE TABLE IF NOT EXISTS player_balance (
  user_id    UUID PRIMARY KEY REFERENCES auth.users(id) ON DELETE CASCADE,
  balance    NUMERIC(10,2) NOT NULL DEFAULT 0,
  currency   TEXT NOT NULL DEFAULT 'GBP',
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

ALTER TABLE player_balance ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'player_balance_service_all' AND tablename = 'player_balance') THEN
    CREATE POLICY player_balance_service_all ON player_balance
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'player_balance_own_select' AND tablename = 'player_balance') THEN
    CREATE POLICY player_balance_own_select ON player_balance
      FOR SELECT USING (auth.uid() = user_id);
  END IF;
END $$;

CREATE TABLE IF NOT EXISTS balance_transactions (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id     UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
  amount      NUMERIC(10,2) NOT NULL,
  description TEXT,
  venue_id    UUID REFERENCES venues(id),
  session_id  UUID REFERENCES venue_sessions(id),
  created_at  TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_balance_transactions_user ON balance_transactions(user_id, created_at DESC);

ALTER TABLE balance_transactions ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'balance_tx_service_all' AND tablename = 'balance_transactions') THEN
    CREATE POLICY balance_tx_service_all ON balance_transactions
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'balance_tx_own_select' AND tablename = 'balance_transactions') THEN
    CREATE POLICY balance_tx_own_select ON balance_transactions
      FOR SELECT USING (auth.uid() = user_id);
  END IF;
END $$;
