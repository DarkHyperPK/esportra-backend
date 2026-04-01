-- Flush all cached IGDB assets so games re-fetch fresh artwork from IGDB API.
-- This fixes mismatched artwork (e.g. Dota 2 showing Rocket League assets).
UPDATE games_metadata SET igdb_assets = NULL;
