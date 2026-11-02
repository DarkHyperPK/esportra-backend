-- A reported map the other team never answers is confirmed automatically once its deadline passes,
-- so a team that goes quiet cannot stall the series.
ALTER TABLE public.scrim_session_games
    ADD COLUMN IF NOT EXISTS accept_by      TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS auto_confirmed BOOLEAN     NOT NULL DEFAULT FALSE;

CREATE INDEX IF NOT EXISTS idx_scrim_session_games_due
    ON public.scrim_session_games (accept_by)
    WHERE status = 'reported';
