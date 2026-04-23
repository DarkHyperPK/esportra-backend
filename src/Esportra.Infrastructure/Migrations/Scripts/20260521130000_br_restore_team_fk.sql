-- Restore nullable REFERENCES teams FK on br_group_teams.team_id
-- The previous migration made team_id nullable but dropped the FK entirely,
-- breaking ON DELETE CASCADE for team deletions.
-- This patch re-adds the FK as nullable with ON DELETE CASCADE.

DO $$
BEGIN
    -- br_group_teams: re-add FK if missing
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_type = 'FOREIGN KEY'
          AND table_name      = 'br_group_teams'
          AND constraint_name = 'br_group_teams_team_id_fkey'
    ) THEN
        ALTER TABLE br_group_teams
            ADD CONSTRAINT br_group_teams_team_id_fkey
            FOREIGN KEY (team_id) REFERENCES teams(id) ON DELETE CASCADE;
    END IF;
END;
$$;

DO $$
BEGIN
    -- br_round_results: re-add FK if missing
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_type = 'FOREIGN KEY'
          AND table_name      = 'br_round_results'
          AND constraint_name = 'br_round_results_team_id_fkey'
    ) THEN
        ALTER TABLE br_round_results
            ADD CONSTRAINT br_round_results_team_id_fkey
            FOREIGN KEY (team_id) REFERENCES teams(id) ON DELETE CASCADE;
    END IF;
END;
$$;
