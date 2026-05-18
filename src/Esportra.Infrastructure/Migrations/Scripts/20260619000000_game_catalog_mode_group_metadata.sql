-- Adds optional display metadata for grouped playable modes.
-- This is additive so existing active catalog versions remain valid until the
-- packaged catalog import activates a version with grouped modes.

ALTER TABLE public.game_catalog_game_modes
    ADD COLUMN IF NOT EXISTS display_group TEXT,
    ADD COLUMN IF NOT EXISTS variant_label TEXT;
