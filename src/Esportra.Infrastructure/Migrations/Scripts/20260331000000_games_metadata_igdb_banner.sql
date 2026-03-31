-- Add IGDB columns to games_metadata for caching IGDB artwork/video assets
ALTER TABLE public.games_metadata ADD COLUMN IF NOT EXISTS igdb_banner TEXT;
ALTER TABLE public.games_metadata ADD COLUMN IF NOT EXISTS igdb_assets JSONB;
