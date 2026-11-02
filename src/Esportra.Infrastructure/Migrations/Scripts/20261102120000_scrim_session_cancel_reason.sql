-- Scrim sessions: record why a session was cancelled.
--
-- Until now a check-in timeout (no-show) and a manual cancel both ended as
-- status = 'cancelled' with no reason, so team reliability stats could not tell
-- them apart. NULL means "unknown / cancelled before this migration"; existing
-- rows are intentionally NOT backfilled.

ALTER TABLE public.scrim_sessions
    ADD COLUMN IF NOT EXISTS cancelled_reason     TEXT NULL,
    ADD COLUMN IF NOT EXISTS cancelled_by_team_id UUID NULL
        REFERENCES public.teams (id) ON DELETE SET NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_sessions_cancelled_reason') THEN
        ALTER TABLE public.scrim_sessions
            ADD CONSTRAINT chk_scrim_sessions_cancelled_reason
            CHECK (cancelled_reason IS NULL
                   OR cancelled_reason IN ('checkin_timeout', 'cancelled_by_team', 'listing_closed', 'admin'));
    END IF;
END $$;

COMMENT ON COLUMN public.scrim_sessions.cancelled_reason IS
    'Why the session was cancelled: checkin_timeout | cancelled_by_team | listing_closed | admin. NULL = unknown/legacy.';
