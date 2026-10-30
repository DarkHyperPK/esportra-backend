-- PROJ-077 M7: Session check-in tracking
-- Extends scrim_sessions with team1_checked_in_at and team2_checked_in_at.
-- Null = not yet checked in; set when each team confirms they are ready.
-- Existing RLS (either team's captain/manager for ALL operations) covers
-- these columns automatically — no policy changes needed.

ALTER TABLE public.scrim_sessions
    ADD COLUMN IF NOT EXISTS team1_checked_in_at TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS team2_checked_in_at TIMESTAMPTZ NULL;
