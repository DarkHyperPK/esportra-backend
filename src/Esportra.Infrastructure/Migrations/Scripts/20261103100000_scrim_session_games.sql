-- Per-map results for scrim series. Each map of a Bo1/Bo3/Bo5 is reported, confirmed and
-- (if needed) disputed on its own; the session's series score is derived from the settled maps.
-- Writes go through the API (service role); team staff may read their own sessions' games.

CREATE TABLE IF NOT EXISTS public.scrim_session_games (
    id                    UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id            UUID        NOT NULL REFERENCES public.scrim_sessions(id) ON DELETE CASCADE,
    game_number           INT         NOT NULL,
    status                TEXT        NOT NULL DEFAULT 'reported',
    map_name              TEXT        NULL,
    map_id                TEXT        NULL,
    team1_score           INT         NOT NULL,
    team2_score           INT         NOT NULL,
    -- 1 or 2 once the map counts for a team; NULL while unsettled or void (no contest).
    winner_slot           INT         NULL,
    source                TEXT        NOT NULL DEFAULT 'manual',
    riot_match_id         TEXT        NULL,
    match_data            JSONB       NULL,
    screenshot_urls       TEXT[]      NOT NULL DEFAULT ARRAY[]::TEXT[],
    notes                 TEXT        NULL,
    reported_by_team_id   UUID        NOT NULL REFERENCES public.teams(id),
    reported_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    confirmed_by_team_id  UUID        NULL REFERENCES public.teams(id),
    confirmed_at          TIMESTAMPTZ NULL,
    disputed_by_team_id   UUID        NULL REFERENCES public.teams(id),
    disputed_at           TIMESTAMPTZ NULL,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT now()
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_scrim_session_game') THEN
        ALTER TABLE public.scrim_session_games
            ADD CONSTRAINT uq_scrim_session_game UNIQUE (session_id, game_number);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_game_number') THEN
        ALTER TABLE public.scrim_session_games
            ADD CONSTRAINT chk_scrim_game_number CHECK (game_number BETWEEN 1 AND 10);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_game_status') THEN
        ALTER TABLE public.scrim_session_games
            ADD CONSTRAINT chk_scrim_game_status
            CHECK (status IN ('reported', 'confirmed', 'disputed', 'resolved'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_game_source') THEN
        ALTER TABLE public.scrim_session_games
            ADD CONSTRAINT chk_scrim_game_source CHECK (source IN ('manual', 'riot', 'ocr'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_game_winner') THEN
        ALTER TABLE public.scrim_session_games
            ADD CONSTRAINT chk_scrim_game_winner CHECK (winner_slot IS NULL OR winner_slot IN (1, 2));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_game_scores') THEN
        ALTER TABLE public.scrim_session_games
            ADD CONSTRAINT chk_scrim_game_scores CHECK (team1_score >= 0 AND team2_score >= 0);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_scrim_session_games_session ON public.scrim_session_games (session_id);

-- A dispute may be about one map; NULL keeps the old whole-session meaning.
ALTER TABLE public.scrim_disputes
    ADD COLUMN IF NOT EXISTS game_number INT NULL;

ALTER TABLE public.scrim_session_games ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS scrim_session_games_read ON public.scrim_session_games;
CREATE POLICY scrim_session_games_read
    ON public.scrim_session_games FOR SELECT TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.scrim_sessions ss
            JOIN public.team_members tm ON tm.team_id IN (ss.team1_id, ss.team2_id)
            WHERE ss.id = scrim_session_games.session_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    );

DROP POLICY IF EXISTS scrim_session_games_service ON public.scrim_session_games;
CREATE POLICY scrim_session_games_service
    ON public.scrim_session_games FOR ALL TO service_role
    USING (true) WITH CHECK (true);
