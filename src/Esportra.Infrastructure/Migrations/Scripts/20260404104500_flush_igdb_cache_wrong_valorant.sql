-- Flush stale IGDB cache entries that may have cached wrong game artwork
-- (e.g. "Grit and Valor" cached under "Valorant" key).
-- After this, requests will re-fetch from IGDB with name validation.
UPDATE public.games_metadata SET igdb_assets = NULL WHERE igdb_assets IS NOT NULL;
