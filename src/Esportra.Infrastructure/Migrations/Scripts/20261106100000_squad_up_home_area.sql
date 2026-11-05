-- Squad Up matches players by the area they live in (one pick, e.g. 'south-asia'), not by a list of countries.
-- Sessions and lobbies are ephemeral (stale rows are deleted on the next start or closed on read), so no backfill:
-- rows without an area simply match nobody until the player starts again.
-- The old country columns stay for display; profile country is optional now, so the lobby column may be NULL.
ALTER TABLE public.lfg_swipe_sessions ADD COLUMN IF NOT EXISTS area TEXT;
ALTER TABLE public.squad_up_lobbies ADD COLUMN IF NOT EXISTS area TEXT;
ALTER TABLE public.squad_up_lobbies ALTER COLUMN country_code DROP NOT NULL;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_lfg_swipe_sessions_area') THEN
    ALTER TABLE public.lfg_swipe_sessions ADD CONSTRAINT chk_lfg_swipe_sessions_area CHECK (area IS NULL OR area IN (
      'south-asia', 'middle-east', 'europe', 'central-asia', 'east-asia', 'southeast-asia', 'oceania',
      'north-america', 'latin-america', 'africa'));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_squad_up_lobbies_area') THEN
    ALTER TABLE public.squad_up_lobbies ADD CONSTRAINT chk_squad_up_lobbies_area CHECK (area IS NULL OR area IN (
      'south-asia', 'middle-east', 'europe', 'central-asia', 'east-asia', 'southeast-asia', 'oceania',
      'north-america', 'latin-america', 'africa'));
  END IF;
END $$;
