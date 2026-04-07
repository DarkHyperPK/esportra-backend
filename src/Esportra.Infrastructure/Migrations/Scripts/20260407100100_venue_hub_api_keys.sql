-- ============================================================
-- Per-venue hub API keys
--
-- Problem:  All venues currently share one static API key for
--           the cloud hub connection. A single compromise
--           exposes every venue on the platform.
--
-- Solution: Each venue gets its own API key. The database
--           stores only the bcrypt hash; the plaintext is
--           shown to the owner exactly once at generation time.
--
-- Adds:     hub_api_key_hash, hub_key_issued_at,
--           hub_key_rotated_at columns on public.venues.
--           generate_venue_hub_key() helper function.
--           Backfills published venues with a temporary key.
-- ============================================================


-- ===================
-- 1. New columns
-- ===================
ALTER TABLE public.venues
  ADD COLUMN IF NOT EXISTS hub_api_key_hash   TEXT,
  ADD COLUMN IF NOT EXISTS hub_key_issued_at  TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS hub_key_rotated_at TIMESTAMPTZ;


-- ===================
-- 2. Helper function: generate a random 32-char hex key
--    Returns PLAINTEXT. Caller (backend API) is responsible
--    for hashing with bcrypt before storing via service_role.
-- ===================
CREATE OR REPLACE FUNCTION public.generate_venue_hub_key()
RETURNS TEXT
LANGUAGE sql
VOLATILE
SECURITY INVOKER
SET search_path = public
AS $$
  SELECT encode(extensions.gen_random_bytes(16), 'hex');
$$;


-- ===================
-- 3. Backfill published venues
--    Generate a temporary key and store its bcrypt hash so
--    existing published venues are not left without a key.
--    Uses pgcrypto from the extensions schema (Supabase).
--    Idempotent: skips venues that already have a hash.
-- ===================
DO $$
DECLARE
  v   RECORD;
  raw_key TEXT;
BEGIN
  FOR v IN
    SELECT id
    FROM   public.venues
    WHERE  status = 'published'
      AND  hub_api_key_hash IS NULL
  LOOP
    raw_key := encode(extensions.gen_random_bytes(16), 'hex');

    UPDATE public.venues
    SET    hub_api_key_hash  = extensions.crypt(raw_key, extensions.gen_salt('bf')),
           hub_key_issued_at = NOW()
    WHERE  id = v.id;
  END LOOP;
END;
$$;


-- ===================
-- 4. RLS — security notes
-- ===================
-- The venues table already has ROW LEVEL SECURITY enabled
-- (20260303100000_venue_overhaul.sql) with these SELECT policies:
--
--   • Public:   can see rows with status = 'published'
--   • Owner:    can see their own rows (owner_id = auth.uid())
--   • Admin:    can see all rows
--
-- PostgreSQL RLS is ROW-level, not COLUMN-level. We cannot
-- create a policy that hides hub_api_key_hash from non-owners
-- while still exposing other columns on the same row.
--
-- Mitigations (enforced in the backend API):
--   1. Public-facing queries (list, detail) MUST NOT select
--      hub_api_key_hash — use explicit column lists, not SELECT *.
--   2. Only the owner-facing "Hub Settings" endpoint returns
--      key metadata (issued_at, rotated_at). The plaintext key
--      is returned exactly once at generation time.
--   3. The stored value is a bcrypt hash — even if the hash
--      leaks, the original key cannot be recovered.
--   4. service_role (backend) bypasses RLS and is used for
--      all key lifecycle operations (issue, rotate, revoke).


-- ===================
-- 5. Table-level GRANT
--    service_role bypasses RLS but still needs a table-level
--    privilege. GRANT is idempotent — safe to re-run.
-- ===================
GRANT ALL                    ON public.venues TO service_role;
GRANT SELECT, INSERT, UPDATE ON public.venues TO authenticated;
