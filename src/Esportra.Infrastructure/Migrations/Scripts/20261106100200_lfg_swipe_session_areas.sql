-- Replaces the single area column with a text[] so players can pick multiple regions to match in.
ALTER TABLE public.lfg_swipe_sessions ADD COLUMN IF NOT EXISTS areas text[] NOT NULL DEFAULT '{}';

-- PRE-FLIGHT: backfill so any in-flight sessions keep their match pool.
-- Expected: rowcount equals sessions where area IS NOT NULL and areas is still empty.
UPDATE public.lfg_swipe_sessions
SET areas = ARRAY[area]
WHERE area IS NOT NULL AND cardinality(areas) = 0;
