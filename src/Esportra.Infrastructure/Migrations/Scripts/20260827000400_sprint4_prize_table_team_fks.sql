-- M10: Add FK team_id → teams(id) ON DELETE RESTRICT on prize tables
-- ON DELETE RESTRICT: prevent silently orphaning prize records when a team is deleted

ALTER TABLE tournament_placements
    ADD CONSTRAINT tournament_placements_team_id_fkey
    FOREIGN KEY (team_id) REFERENCES teams(id) ON DELETE RESTRICT;

ALTER TABLE tournament_cash_payouts
    ADD CONSTRAINT tournament_cash_payouts_team_id_fkey
    FOREIGN KEY (team_id) REFERENCES teams(id) ON DELETE RESTRICT;

ALTER TABLE tournament_reward_distributions
    ADD CONSTRAINT tournament_reward_distributions_team_id_fkey
    FOREIGN KEY (team_id) REFERENCES teams(id) ON DELETE RESTRICT;
