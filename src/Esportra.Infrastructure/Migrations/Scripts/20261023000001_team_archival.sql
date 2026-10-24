-- Add soft-archival columns to teams.
-- Archived teams are hidden from active queries but retained for history.

ALTER TABLE public.teams
    ADD COLUMN IF NOT EXISTS is_archived   BOOLEAN     NOT NULL DEFAULT FALSE;

ALTER TABLE public.teams
    ADD COLUMN IF NOT EXISTS archived_at   TIMESTAMPTZ;

ALTER TABLE public.teams
    ADD COLUMN IF NOT EXISTS archived_reason TEXT;

-- Partial index to keep active-team queries fast; archived rows are excluded automatically.
CREATE INDEX IF NOT EXISTS idx_teams_is_archived
    ON public.teams(is_archived)
    WHERE is_archived = FALSE;
