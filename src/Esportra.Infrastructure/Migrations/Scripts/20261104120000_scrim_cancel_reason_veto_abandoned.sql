-- Scrim sessions: a veto nobody acted on for the inactivity window is cancelled with its own reason,
-- instead of NULL (which reads as "unknown/legacy" and was shown to users as a check-in timeout).
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_sessions_cancelled_reason')
       AND NOT EXISTS (SELECT 1 FROM pg_constraint
                       WHERE conname = 'chk_scrim_sessions_cancelled_reason'
                         AND pg_get_constraintdef(oid) LIKE '%veto_abandoned%') THEN
        ALTER TABLE public.scrim_sessions DROP CONSTRAINT chk_scrim_sessions_cancelled_reason;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_sessions_cancelled_reason') THEN
        ALTER TABLE public.scrim_sessions
            ADD CONSTRAINT chk_scrim_sessions_cancelled_reason
            CHECK (cancelled_reason IS NULL
                   OR cancelled_reason IN ('checkin_timeout', 'cancelled_by_team', 'listing_closed', 'admin', 'veto_abandoned'));
    END IF;
END $$;

COMMENT ON COLUMN public.scrim_sessions.cancelled_reason IS
    'Why the session was cancelled: checkin_timeout | cancelled_by_team | listing_closed | admin | veto_abandoned. NULL = unknown/legacy.';
