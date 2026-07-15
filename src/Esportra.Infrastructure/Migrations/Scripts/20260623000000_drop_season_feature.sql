-- Remove season feature tables from staging databases that already applied season migrations.
-- Idempotent: safe on databases that never had season tables.

DROP TABLE IF EXISTS public.season_announcements CASCADE;
DROP TABLE IF EXISTS public.season_staff CASCADE;
DROP TABLE IF EXISTS public.season_advancement_rules CASCADE;
DROP TABLE IF EXISTS public.season_point_rules CASCADE;
DROP TABLE IF EXISTS public.season_qualification_records CASCADE;
DROP TABLE IF EXISTS public.season_standings CASCADE;
DROP TABLE IF EXISTS public.season_participants CASCADE;
DROP TABLE IF EXISTS public.season_tournaments CASCADE;
DROP TABLE IF EXISTS public.season_nodes CASCADE;
DROP TABLE IF EXISTS public.seasons CASCADE;
