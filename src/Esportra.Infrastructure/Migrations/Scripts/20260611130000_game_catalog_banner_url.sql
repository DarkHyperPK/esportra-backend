-- Canonical absolute HTTPS banner URL for game catalog entries (emails, invites, marketing).
ALTER TABLE public.game_catalog_games
    ADD COLUMN IF NOT EXISTS banner_url TEXT;

ALTER TABLE public.game_catalog_games
    DROP CONSTRAINT IF EXISTS chk_game_catalog_games_banner_url_https;

ALTER TABLE public.game_catalog_games
    ADD CONSTRAINT chk_game_catalog_games_banner_url_https
        CHECK (banner_url IS NULL OR banner_url ~ '^https://');

CREATE INDEX IF NOT EXISTS idx_game_catalog_games_banner_url
    ON public.game_catalog_games (slug)
    WHERE banner_url IS NOT NULL;
