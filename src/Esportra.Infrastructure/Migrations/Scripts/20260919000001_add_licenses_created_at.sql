-- licenses.created_at was missing: table predates DbUp tracking; CREATE TABLE IF NOT EXISTS
-- in 20260303110000 was a no-op against the pre-existing Supabase-native schema.
-- GET /api/profiles/{id}/licenses selects created_at at byte position 83 → 42703 error.

ALTER TABLE public.licenses
    ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ DEFAULT NOW();

-- PRE-FLIGHT: Run on production before deploying, confirm expected count before proceeding:
-- SELECT COUNT(*) FROM public.licenses WHERE created_at IS NULL;
-- Expected: small number (all pre-existing license rows); backfill from issued_at for accuracy
UPDATE public.licenses
SET created_at = issued_at
WHERE created_at IS NULL;
