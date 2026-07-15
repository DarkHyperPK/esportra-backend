-- Backfill catalog asset columns from packaged seed JSON stored in raw.
UPDATE public.game_catalog_games
SET
    logo_url = COALESCE(NULLIF(logo_url, ''), NULLIF(raw->>'logo', '')),
    icon_url = COALESCE(NULLIF(icon_url, ''), NULLIF(raw->>'icon', '')),
    cover_url = COALESCE(NULLIF(cover_url, ''), NULLIF(raw->>'cover', ''))
WHERE raw IS NOT NULL;
