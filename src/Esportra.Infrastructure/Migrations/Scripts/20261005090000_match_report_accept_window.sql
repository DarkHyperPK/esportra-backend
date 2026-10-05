-- Opponent response window for match result reports.
--
-- A submitted report waits for the opponent until accept_by; after that the API accepts it so a team
-- that never answers can't stall the bracket. auto_accepted records why the system (not a captain)
-- accepted it: the window ran out, or Riot's record of the match confirmed every player and number.
ALTER TABLE public.match_result_reports
    ADD COLUMN IF NOT EXISTS accept_by timestamptz,
    ADD COLUMN IF NOT EXISTS auto_accepted text;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'match_result_reports_auto_accepted_check'
    ) THEN
        ALTER TABLE public.match_result_reports
            ADD CONSTRAINT match_result_reports_auto_accepted_check
            CHECK (auto_accepted IS NULL OR auto_accepted IN ('timeout', 'riot_confirmed'));
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS match_result_reports_accept_by_pending_idx
    ON public.match_result_reports (accept_by)
    WHERE status = 'pending';
