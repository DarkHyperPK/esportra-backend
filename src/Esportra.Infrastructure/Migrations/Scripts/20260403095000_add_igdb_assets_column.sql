-- Add igdb_assets JSONB column to games_metadata for caching IGDB artwork/video responses
ALTER TABLE public.games_metadata ADD COLUMN IF NOT EXISTS igdb_assets JSONB;
