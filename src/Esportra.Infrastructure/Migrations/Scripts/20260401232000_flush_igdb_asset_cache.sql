-- Flush all cached IGDB assets so games re-fetch fresh artwork from IGDB API.
-- This fixes mismatched artwork (e.g. Dota 2 showing Rocket League assets).
-- Use DO block to handle case where igdb_assets column may not exist yet.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'games_metadata' AND column_name = 'igdb_assets'
    ) THEN
        EXECUTE 'UPDATE public.games_metadata SET igdb_assets = NULL';
    END IF;
END $$;
