-- Part A: Add privacy_settings column to profiles.
-- Keys consumed at runtime by the linked-accounts endpoint:
--   show_riot_account  (bool, default true)
--   show_steam_account (bool, default true)

ALTER TABLE profiles ADD COLUMN IF NOT EXISTS privacy_settings JSONB NOT NULL DEFAULT '{}';

-- Part B: RLS audit for tables exposed by new public unauthenticated endpoints.
-- Enable RLS on each table (no-op if already enabled) and add a public SELECT policy
-- if one does not already exist.

-- team_members
ALTER TABLE team_members ENABLE ROW LEVEL SECURITY;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'team_members' AND policyname = 'team_members_public_select'
  ) THEN
    CREATE POLICY "team_members_public_select" ON team_members FOR SELECT USING (true);
  END IF;
END $$;

-- tournament_participants
ALTER TABLE tournament_participants ENABLE ROW LEVEL SECURITY;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'tournament_participants' AND policyname = 'tournament_participants_public_select'
  ) THEN
    CREATE POLICY "tournament_participants_public_select" ON tournament_participants FOR SELECT USING (true);
  END IF;
END $$;

-- tournament_placements
ALTER TABLE tournament_placements ENABLE ROW LEVEL SECURITY;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'tournament_placements' AND policyname = 'tournament_placements_public_select'
  ) THEN
    CREATE POLICY "tournament_placements_public_select" ON tournament_placements FOR SELECT USING (true);
  END IF;
END $$;

-- riot_accounts
ALTER TABLE riot_accounts ENABLE ROW LEVEL SECURITY;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'riot_accounts' AND policyname = 'riot_accounts_public_select'
  ) THEN
    CREATE POLICY "riot_accounts_public_select" ON riot_accounts FOR SELECT USING (true);
  END IF;
END $$;

-- player_steam_accounts
ALTER TABLE player_steam_accounts ENABLE ROW LEVEL SECURITY;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'player_steam_accounts' AND policyname = 'player_steam_accounts_public_select'
  ) THEN
    CREATE POLICY "player_steam_accounts_public_select" ON player_steam_accounts FOR SELECT USING (true);
  END IF;
END $$;
