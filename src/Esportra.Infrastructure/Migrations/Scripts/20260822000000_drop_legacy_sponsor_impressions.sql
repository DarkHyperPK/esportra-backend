-- Drop legacy sponsor impressions pipeline: sponsor_impressions table,
-- daily_sponsor_stats table, and their associated triggers and functions.
-- The new analytics pipeline (sponsor_analytics_events / sponsor_daily_totals)
-- replaced these in 20260717190000_sponsor_analytics_domain.sql. Nothing in
-- the application code has written to or read from these objects since then.

-- Drop tables with CASCADE (cascades to triggers automatically, keeps this
-- idempotent — DROP TRIGGER ... ON <table> throws if the table is already gone).
DROP TABLE IF EXISTS public.sponsor_impressions CASCADE;
DROP TABLE IF EXISTS public.daily_sponsor_stats CASCADE;

-- Drop orphaned functions
DROP FUNCTION IF EXISTS public.handle_new_sponsor_interaction();
DROP FUNCTION IF EXISTS public.fn_aggregate_sponsor_impression();
DROP FUNCTION IF EXISTS public.refresh_daily_sponsor_stats();
DROP FUNCTION IF EXISTS public.rollup_daily_sponsor_stats();
