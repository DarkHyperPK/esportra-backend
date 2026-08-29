-- H2: stage_participants.stage_id FK (orphaned rows on stage delete)
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'stage_participants_stage_id_fkey') THEN
    ALTER TABLE stage_participants
        ADD CONSTRAINT stage_participants_stage_id_fkey
        FOREIGN KEY (stage_id) REFERENCES tournament_stages(id) ON DELETE CASCADE;
  END IF;
END $$;

-- H3: tournament_stages.format CHECK constraint
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_stage_format') THEN
    ALTER TABLE tournament_stages
        ADD CONSTRAINT chk_stage_format CHECK (
            format IN ('single_elimination','double_elimination','round_robin','swiss','battle_royale')
        );
  END IF;
END $$;

-- H4: Normalize tournaments.format to lowercase_underscore (matches stage format values)
UPDATE tournaments
SET format = 'single_elimination'
WHERE format = 'Single Elimination';

-- H8: match_map_vetos.stage_id FK — change RESTRICT to CASCADE
-- DROP IF EXISTS + ADD is intentionally idempotent (replaces old constraint with CASCADE)
ALTER TABLE match_map_vetos
    DROP CONSTRAINT IF EXISTS match_map_vetos_stage_id_fkey;
ALTER TABLE match_map_vetos
    ADD CONSTRAINT match_map_vetos_stage_id_fkey
    FOREIGN KEY (stage_id) REFERENCES tournament_stages(id) ON DELETE CASCADE;

-- H9: match_completed_events indexes (polled by status, looked up by match_id)
CREATE INDEX IF NOT EXISTS idx_match_completed_events_status
    ON match_completed_events(status) WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS idx_match_completed_events_match
    ON match_completed_events(match_id);

-- M1: brkt_versions.stage_id index (seed-bracket lookup)
CREATE INDEX IF NOT EXISTS idx_brkt_versions_stage_id
    ON brkt_versions(stage_id);

-- M2: stage_participants lookup indexes
CREATE INDEX IF NOT EXISTS idx_stage_participants_stage_id
    ON stage_participants(stage_id);
CREATE INDEX IF NOT EXISTS idx_stage_participants_team_id
    ON stage_participants(team_id);

-- M15: Prevent duplicate stage+team registrations
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_stage_participants_stage_team') THEN
    ALTER TABLE stage_participants
        ADD CONSTRAINT uq_stage_participants_stage_team UNIQUE (stage_id, team_id);
  END IF;
END $$;
