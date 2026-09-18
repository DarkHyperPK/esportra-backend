-- user_statistics: per-user aggregate stats (tournaments, placements, prizes, games).
-- Populated and updated by backend domain logic; updated_at maintained by trigger.

CREATE TABLE IF NOT EXISTS user_statistics (
    user_id             UUID        PRIMARY KEY REFERENCES profiles(id) ON DELETE CASCADE,
    tournaments_entered INT         NOT NULL DEFAULT 0,
    tournaments_won     INT         NOT NULL DEFAULT 0,
    best_placement      INT,
    total_prize_cents   BIGINT      NOT NULL DEFAULT 0,
    games_played        INT         NOT NULL DEFAULT 0,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- RLS
ALTER TABLE user_statistics ENABLE ROW LEVEL SECURITY;

-- Public read: profile stats are visible to everyone
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'user_statistics' AND policyname = 'user_statistics_public_select'
  ) THEN
    CREATE POLICY "user_statistics_public_select" ON user_statistics FOR SELECT USING (true);
  END IF;
END $$;

-- Self insert: a user may only create their own stats row
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'user_statistics' AND policyname = 'user_statistics_self_insert'
  ) THEN
    CREATE POLICY "user_statistics_self_insert" ON user_statistics FOR INSERT WITH CHECK (auth.uid() = user_id);
  END IF;
END $$;

-- Self update: a user may only update their own stats row
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'user_statistics' AND policyname = 'user_statistics_self_update'
  ) THEN
    CREATE POLICY "user_statistics_self_update" ON user_statistics FOR UPDATE USING (auth.uid() = user_id);
  END IF;
END $$;

-- updated_at trigger function (CREATE OR REPLACE is idempotent)
CREATE OR REPLACE FUNCTION set_updated_at()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
  NEW.updated_at = NOW();
  RETURN NEW;
END $$;

-- Attach the trigger only if it does not already exist
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_trigger
    WHERE tgname = 'trg_user_statistics_updated_at'
  ) THEN
    CREATE TRIGGER trg_user_statistics_updated_at
      BEFORE UPDATE ON user_statistics
      FOR EACH ROW EXECUTE FUNCTION set_updated_at();
  END IF;
END $$;
