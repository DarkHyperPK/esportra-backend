-- Add igdb_banner column to games_metadata for caching IGDB artwork URLs
ALTER TABLE public.games_metadata ADD COLUMN IF NOT EXISTS igdb_banner TEXT;
