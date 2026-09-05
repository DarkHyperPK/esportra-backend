-- M10: Add FK team_id → teams(id) ON DELETE RESTRICT on prize tables
-- ON DELETE RESTRICT: prevent silently orphaning prize records when a team is deleted
--
-- Pre-flight: delete any orphaned rows where team_id no longer exists in teams.
-- These tables were created without a FK, so stale test data may reference deleted teams.
DELETE FROM tournament_placements
WHERE team_id IS NOT NULL AND team_id NOT IN (SELECT id FROM teams);

DELETE FROM tournament_cash_payouts
WHERE team_id IS NOT NULL AND team_id NOT IN (SELECT id FROM teams);

DELETE FROM tournament_reward_distributions
WHERE team_id IS NOT NULL AND team_id NOT IN (SELECT id FROM teams);

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'tournament_placements_team_id_fkey') THEN
    ALTER TABLE tournament_placements
        ADD CONSTRAINT tournament_placements_team_id_fkey
        FOREIGN KEY (team_id) REFERENCES teams(id) ON DELETE RESTRICT;
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'tournament_cash_payouts_team_id_fkey') THEN
    ALTER TABLE tournament_cash_payouts
        ADD CONSTRAINT tournament_cash_payouts_team_id_fkey
        FOREIGN KEY (team_id) REFERENCES teams(id) ON DELETE RESTRICT;
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'tournament_reward_distributions_team_id_fkey') THEN
    ALTER TABLE tournament_reward_distributions
        ADD CONSTRAINT tournament_reward_distributions_team_id_fkey
        FOREIGN KEY (team_id) REFERENCES teams(id) ON DELETE RESTRICT;
  END IF;
END $$;
