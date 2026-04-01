-- Flush all cached IGDB assets so games re-fetch fresh artwork from IGDB API.
-- This fixes mismatched artwork (e.g. Dota 2 showing Rocket League assets).
-- First ensure the igdb_assets column exists, then flush it.
ALTER TABLE public.games_metadata ADD COLUMN IF NOT EXISTS igdb_assets jsonb;
UPDATE public.games_metadata SET igdb_assets = NULL;
