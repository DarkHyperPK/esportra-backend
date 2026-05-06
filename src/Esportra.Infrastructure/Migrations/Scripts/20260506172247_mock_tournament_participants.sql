-- Mock Tournament Mode: flag participants as fictitious so organizers can
-- simulate brackets in draft tournaments without involving real users.

-- Add is_mock flag to tournament_participants
ALTER TABLE tournament_participants
    ADD COLUMN IF NOT EXISTS is_mock BOOLEAN NOT NULL DEFAULT FALSE;

-- Relax user_id NOT NULL so mock rows can have null user_id
ALTER TABLE tournament_participants
    ALTER COLUMN user_id DROP NOT NULL;

-- Enforce: real participants must have a user_id; mock ones may not
ALTER TABLE tournament_participants
    DROP CONSTRAINT IF EXISTS chk_mock_or_user_id;
ALTER TABLE tournament_participants
    ADD CONSTRAINT chk_mock_or_user_id
        CHECK (is_mock = TRUE OR user_id IS NOT NULL);

-- Partial index for fast lookup of mock participants per tournament
CREATE INDEX IF NOT EXISTS idx_tp_mock_tournament
    ON tournament_participants (tournament_id)
    WHERE is_mock = TRUE;

-- Grants (run as supabase_admin via PostgresMigrations connection string)
GRANT SELECT, INSERT, UPDATE, DELETE ON tournament_participants TO service_role;
GRANT SELECT ON tournament_participants TO authenticated;
