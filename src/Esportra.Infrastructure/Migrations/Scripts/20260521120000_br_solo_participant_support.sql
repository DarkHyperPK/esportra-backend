-- ============================================================================
-- Migration: BR Solo Participant Support
-- Purpose:   Allow solo players (no team) in BR group/round tables by making
--            team_id nullable (drop FK + NOT NULL) and adding participant_id.
-- ============================================================================

-- ── 1. br_group_teams: make team_id nullable, add participant_id ─────────────

-- Drop the NOT NULL constraint on team_id
DO $$ BEGIN
  IF EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_name = 'br_group_teams' AND column_name = 'team_id' AND is_nullable = 'NO'
  ) THEN
    ALTER TABLE br_group_teams ALTER COLUMN team_id DROP NOT NULL;
  END IF;
END $$;

-- Drop the FK constraint on team_id (if it exists)
DO $$ BEGIN
  IF EXISTS (
    SELECT 1 FROM information_schema.table_constraints tc
    JOIN information_schema.key_column_usage kcu ON kcu.constraint_name = tc.constraint_name
    WHERE tc.table_name = 'br_group_teams' AND tc.constraint_type = 'FOREIGN KEY'
      AND kcu.column_name = 'team_id'
  ) THEN
    ALTER TABLE br_group_teams DROP CONSTRAINT IF EXISTS br_group_teams_team_id_fkey;
  END IF;
END $$;

-- Add participant_id column if not exists
ALTER TABLE br_group_teams
  ADD COLUMN IF NOT EXISTS participant_id UUID NULL REFERENCES tournament_participants(id) ON DELETE CASCADE;

-- Add CHECK: exactly one of team_id / participant_id must be non-null
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.table_constraints
    WHERE table_name = 'br_group_teams' AND constraint_name = 'br_group_teams_exactly_one_entity'
  ) THEN
    ALTER TABLE br_group_teams
      ADD CONSTRAINT br_group_teams_exactly_one_entity
      CHECK ((team_id IS NULL) != (participant_id IS NULL));
  END IF;
END $$;

-- Drop the old UNIQUE constraint (group_id, team_id) — it will be replaced by partial indexes
ALTER TABLE br_group_teams DROP CONSTRAINT IF EXISTS br_group_teams_group_id_team_id_key;

-- Partial unique indexes for each entity type
CREATE UNIQUE INDEX IF NOT EXISTS uq_br_group_teams_team
  ON br_group_teams (group_id, team_id)
  WHERE team_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_br_group_teams_participant
  ON br_group_teams (group_id, participant_id)
  WHERE participant_id IS NOT NULL;

-- Index for lookups by participant_id
CREATE INDEX IF NOT EXISTS idx_br_group_teams_participant_id
  ON br_group_teams (participant_id);

-- ── 2. br_round_results: same treatment ──────────────────────────────────────

-- Drop the NOT NULL constraint on team_id
DO $$ BEGIN
  IF EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_name = 'br_round_results' AND column_name = 'team_id' AND is_nullable = 'NO'
  ) THEN
    ALTER TABLE br_round_results ALTER COLUMN team_id DROP NOT NULL;
  END IF;
END $$;

-- Drop the FK constraint on team_id (if it exists)
DO $$ BEGIN
  IF EXISTS (
    SELECT 1 FROM information_schema.table_constraints tc
    JOIN information_schema.key_column_usage kcu ON kcu.constraint_name = tc.constraint_name
    WHERE tc.table_name = 'br_round_results' AND tc.constraint_type = 'FOREIGN KEY'
      AND kcu.column_name = 'team_id'
  ) THEN
    ALTER TABLE br_round_results DROP CONSTRAINT IF EXISTS br_round_results_team_id_fkey;
  END IF;
END $$;

-- Add participant_id column if not exists
ALTER TABLE br_round_results
  ADD COLUMN IF NOT EXISTS participant_id UUID NULL REFERENCES tournament_participants(id) ON DELETE CASCADE;

-- Add CHECK: exactly one of team_id / participant_id must be non-null
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.table_constraints
    WHERE table_name = 'br_round_results' AND constraint_name = 'br_round_results_exactly_one_entity'
  ) THEN
    ALTER TABLE br_round_results
      ADD CONSTRAINT br_round_results_exactly_one_entity
      CHECK ((team_id IS NULL) != (participant_id IS NULL));
  END IF;
END $$;

-- Drop the old UNIQUE constraint (round_id, team_id)
ALTER TABLE br_round_results DROP CONSTRAINT IF EXISTS br_round_results_round_id_team_id_key;

-- Partial unique indexes
CREATE UNIQUE INDEX IF NOT EXISTS uq_br_round_results_team
  ON br_round_results (round_id, team_id)
  WHERE team_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_br_round_results_participant
  ON br_round_results (round_id, participant_id)
  WHERE participant_id IS NOT NULL;

-- Index for lookups by participant_id
CREATE INDEX IF NOT EXISTS idx_br_round_results_participant_id
  ON br_round_results (participant_id);
