-- M9: CHECK constraint on tournament_stages.status
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_stage_status') THEN
    ALTER TABLE tournament_stages
        ADD CONSTRAINT chk_stage_status
        CHECK (status IN ('pending', 'upcoming', 'active', 'completed'));
  END IF;
END $$;

-- M9: CHECK constraint on tournament_disputes.status
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_dispute_status') THEN
    ALTER TABLE tournament_disputes
        ADD CONSTRAINT chk_dispute_status
        CHECK (status IN ('open', 'resolved', 'rejected'));
  END IF;
END $$;
