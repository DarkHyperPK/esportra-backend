-- games_metadata: 7-day DB cache for RAWG API responses
-- Used by /api/games/search backend endpoint

CREATE TABLE IF NOT EXISTS public.games_metadata (
    game_name         TEXT        PRIMARY KEY,
    rawg_id           INTEGER,
    background_image  TEXT,
    last_updated      TIMESTAMPTZ NOT NULL DEFAULT now(),
    rawg_data         JSONB
);

-- Public read access (game images shown to all users)
ALTER TABLE public.games_metadata ENABLE ROW LEVEL SECURITY;

CREATE POLICY "games_metadata_public_read"
    ON public.games_metadata FOR SELECT
    USING (true);
