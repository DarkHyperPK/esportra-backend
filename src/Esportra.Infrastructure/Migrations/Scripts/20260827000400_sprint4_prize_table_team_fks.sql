-- M10: Add FK team_id → teams(id) ON DELETE RESTRICT on prize tables
-- ON DELETE RESTRICT: prevent silently orphaning prize records when a team is deleted

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
