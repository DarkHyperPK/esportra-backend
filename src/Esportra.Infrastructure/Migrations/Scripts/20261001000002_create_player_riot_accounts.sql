CREATE TABLE IF NOT EXISTS player_riot_accounts (
    id             uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id        uuid        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    riot_puuid     text        NOT NULL,
    game_name      text        NOT NULL,  -- "PlayerName"
    tag_line       text        NOT NULL,  -- "NA1"
    region         text,                 -- e.g. 'na', 'eu', 'ap' — null until enriched
    verified_at    timestamptz,           -- null until Riot API confirms ownership
    created_at     timestamptz NOT NULL DEFAULT now()
);

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'player_riot_accounts_user_uniq') THEN
    ALTER TABLE player_riot_accounts ADD CONSTRAINT player_riot_accounts_user_uniq UNIQUE (user_id);
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'player_riot_accounts_puuid_uniq') THEN
    ALTER TABLE player_riot_accounts ADD CONSTRAINT player_riot_accounts_puuid_uniq UNIQUE (riot_puuid);
  END IF;
END $$;

ALTER TABLE player_riot_accounts ENABLE ROW LEVEL SECURITY;

-- Users can manage their own linked account
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'player_riot_accounts' AND policyname = 'own_riot_account'
  ) THEN
    CREATE POLICY "own_riot_account" ON player_riot_accounts
        FOR ALL TO authenticated
        USING (user_id = auth.uid())
        WITH CHECK (user_id = auth.uid());
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS player_riot_accounts_user_idx  ON player_riot_accounts (user_id);
CREATE INDEX IF NOT EXISTS player_riot_accounts_puuid_idx ON player_riot_accounts (riot_puuid);
