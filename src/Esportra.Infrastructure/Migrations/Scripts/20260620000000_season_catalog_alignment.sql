-- Staging-first season/catalog alignment.
-- Adds explicit season-level game mode and region so generated season tournaments
-- inherit one canonical catalog-backed identity instead of node-local guesses.

ALTER TABLE public.seasons
    ADD COLUMN IF NOT EXISTS game_mode TEXT,
    ADD COLUMN IF NOT EXISTS region TEXT,
    ADD COLUMN IF NOT EXISTS catalog_game_slug TEXT;

ALTER TABLE public.season_nodes
    ADD COLUMN IF NOT EXISTS generated_tournament_id UUID NULL REFERENCES public.tournaments(id) ON DELETE SET NULL;

CREATE INDEX IF NOT EXISTS idx_seasons_catalog_game_mode
    ON public.seasons(catalog_game_slug, game_mode)
    WHERE deleted_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_seasons_region
    ON public.seasons(region)
    WHERE deleted_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_season_nodes_generated_tournament
    ON public.season_nodes(generated_tournament_id)
    WHERE generated_tournament_id IS NOT NULL;

UPDATE public.seasons s
SET region = COALESCE(
    (
        SELECT t.region
        FROM public.season_tournaments st
        JOIN public.tournaments t ON t.id = st.tournament_id
        WHERE st.season_id = s.id AND NULLIF(TRIM(t.region), '') IS NOT NULL
        ORDER BY st.season_stage_order ASC, st.created_at ASC
        LIMIT 1
    ),
    (
        SELECT sn.region
        FROM public.season_nodes sn
        WHERE sn.season_id = s.id AND NULLIF(TRIM(sn.region), '') IS NOT NULL
        ORDER BY sn.display_order ASC, sn.created_at ASC
        LIMIT 1
    )
)
WHERE s.region IS NULL;

UPDATE public.seasons s
SET game_mode = (
    SELECT t.game_mode
    FROM public.season_tournaments st
    JOIN public.tournaments t ON t.id = st.tournament_id
    WHERE st.season_id = s.id AND NULLIF(TRIM(t.game_mode), '') IS NOT NULL
    ORDER BY st.season_stage_order ASC, st.created_at ASC
    LIMIT 1
)
WHERE s.game_mode IS NULL;
