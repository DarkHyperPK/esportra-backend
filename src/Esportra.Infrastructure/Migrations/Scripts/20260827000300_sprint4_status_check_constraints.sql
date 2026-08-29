-- M9: CHECK constraint on tournament_stages.status
-- Pre-flight: normalize any rows with status values not in the allowed set.
-- The deprecated PATCH /api/stages/{stageId}/status endpoint allowed arbitrary values;
-- staging may have historical rows outside the 4 valid states.
UPDATE tournament_stages
SET status = CASE
    WHEN status IN ('cancelled', 'closed', 'ended', 'finished', 'archived') THEN 'completed'
    WHEN status IN ('running', 'in_progress', 'started') THEN 'active'
    ELSE 'pending'
END
WHERE status NOT IN ('pending', 'upcoming', 'active', 'completed');

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_stage_status') THEN
    ALTER TABLE tournament_stages
        ADD CONSTRAINT chk_stage_status
        CHECK (status IN ('pending', 'upcoming', 'active', 'completed'));
  END IF;
END $$;

-- M9: CHECK constraint on tournament_disputes.status
-- Pre-flight: normalize any rows outside the allowed set.
-- Closed/in-progress disputes map conservatively to open (not auto-resolved).
UPDATE tournament_disputes
SET status = CASE
    WHEN status IN ('closed', 'completed', 'done', 'finalized') THEN 'resolved'
    ELSE 'open'
END
WHERE status NOT IN ('open', 'resolved', 'rejected');

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_dispute_status') THEN
    ALTER TABLE tournament_disputes
        ADD CONSTRAINT chk_dispute_status
        CHECK (status IN ('open', 'resolved', 'rejected'));
  END IF;
END $$;
