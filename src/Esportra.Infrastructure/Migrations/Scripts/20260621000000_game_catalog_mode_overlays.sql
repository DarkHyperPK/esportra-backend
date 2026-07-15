-- Per-mode catalog overlays: map pool filter + optional feature overrides.

ALTER TABLE public.game_catalog_game_modes
    ADD COLUMN IF NOT EXISTS map_pool_filter TEXT,
    ADD COLUMN IF NOT EXISTS features_override JSONB;

ALTER TABLE public.game_catalog_game_modes
    DROP CONSTRAINT IF EXISTS chk_game_catalog_modes_map_pool_filter;

ALTER TABLE public.game_catalog_game_modes
    ADD CONSTRAINT chk_game_catalog_modes_map_pool_filter
        CHECK (map_pool_filter IS NULL OR map_pool_filter IN ('standard', 'skirmish'));
