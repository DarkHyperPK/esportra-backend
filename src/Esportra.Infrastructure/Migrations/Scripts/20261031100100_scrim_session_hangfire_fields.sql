-- PROJ-078 T1: Scrim session Hangfire job tracking + veto linkage fields.
-- Adds hangfire_job_ids (replaces reminder_sent_at/checkin_window_opened_at polling model),
-- checkin_window_opened_at (still needed as observable signal for frontend),
-- and veto_session_id / raw token columns for map veto integration.
-- Raw token columns are NEVER returned from API responses — GetSessionAsync derives
-- veto_my_token from caller's team membership.

ALTER TABLE public.scrim_sessions
    ADD COLUMN IF NOT EXISTS hangfire_job_ids          JSONB       NULL,
    ADD COLUMN IF NOT EXISTS checkin_window_opened_at  TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS veto_session_id           UUID        NULL
        REFERENCES public.public_veto_sessions(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS veto_team1_token          TEXT        NULL,
    ADD COLUMN IF NOT EXISTS veto_team2_token          TEXT        NULL;

-- Partial index used by Hangfire job queries: sessions scheduled in the near future.
CREATE INDEX IF NOT EXISTS idx_scrim_sessions_scheduled_window
    ON public.scrim_sessions (scheduled_at)
    WHERE status = 'scheduled';
