-- Final season feature teardown: legacy tables, orphaned function/index, tournament JSON keys.
-- Idempotent — safe on databases that never had season objects.

DROP FUNCTION IF EXISTS public.cleanup_deleted_season() CASCADE;

DROP TABLE IF EXISTS public.season_advancement_connections CASCADE;
DROP TABLE IF EXISTS public.season_advancement_records CASCADE;
DROP TABLE IF EXISTS public.season_audit_log CASCADE;
DROP TABLE IF EXISTS public.season_audit_logs CASCADE;
DROP TABLE IF EXISTS public.season_entry_locks CASCADE;
DROP TABLE IF EXISTS public.season_idempotency_keys CASCADE;
DROP TABLE IF EXISTS public.season_points_ledger CASCADE;
DROP TABLE IF EXISTS public.season_points_rules CASCADE;

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

DROP INDEX IF EXISTS public.idx_audit_logs_season;

UPDATE public.tournaments
SET settings = settings - 'seasonId' - 'seasonNodeId' - 'seasonRole'
WHERE settings ?| ARRAY['seasonId', 'seasonNodeId', 'seasonRole'];
