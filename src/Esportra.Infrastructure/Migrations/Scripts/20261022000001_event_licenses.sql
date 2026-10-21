-- ============================================================================
-- DB-1: event_licenses table + quota BEFORE INSERT trigger + RLS
-- PROJ-058 (Event Licensing & Feature Gating)
--
-- Table:
--   public.event_licenses — one license row per tournament; tracks bracket
--   tier, Organizer Pro flag, and free-quota eligibility.
--
-- Trigger:
--   check_free_event_quota() BEFORE INSERT — enforces maximum 2 free-quota
--   events per user per calendar month; raises 'quota_exhausted' on breach.
--
-- RLS: default deny on all operations.
--   SELECT: authenticated user can read their own rows (user_id = auth.uid()).
--   INSERT / UPDATE / DELETE: no authenticated policy — default deny.
--   Writes are performed exclusively by service_role (bypasses RLS).
-- ============================================================================

-- ── 1. Table ─────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.event_licenses (
  id                uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
  tournament_id     uuid        NOT NULL REFERENCES public.tournaments(id),
  user_id           uuid        NOT NULL REFERENCES public.profiles(id),
  organization_id   uuid        NOT NULL REFERENCES public.organizations(id),
  bracket_tier      text        NOT NULL DEFAULT 'free',
  bracket_max_teams int         NOT NULL DEFAULT 32,
  has_organizer_pro bool        NOT NULL DEFAULT false,
  is_free_quota     bool        NOT NULL DEFAULT true,
  status            text        NOT NULL DEFAULT 'active',
  created_at        timestamptz NOT NULL DEFAULT now()
);

-- tournament_id is globally unique: one license per tournament
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'event_licenses_tournament_id_key'
  ) THEN
    ALTER TABLE public.event_licenses
      ADD CONSTRAINT event_licenses_tournament_id_key UNIQUE (tournament_id);
  END IF;
END $$;

-- bracket_tier domain: constrain to the defined tier set
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'event_licenses_bracket_tier_check'
  ) THEN
    ALTER TABLE public.event_licenses
      ADD CONSTRAINT event_licenses_bracket_tier_check
      CHECK (bracket_tier IN ('free', '<=64', '<=128', '<=256', '<=512', 'custom'));
  END IF;
END $$;

-- status domain: only 'active' in this phase
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'event_licenses_status_check'
  ) THEN
    ALTER TABLE public.event_licenses
      ADD CONSTRAINT event_licenses_status_check
      CHECK (status IN ('active'));
  END IF;
END $$;

-- Partial index for the quota trigger query: user_id + is_free_quota + month window
CREATE INDEX IF NOT EXISTS event_licenses_user_free_quota_idx
  ON public.event_licenses (user_id, created_at)
  WHERE is_free_quota = TRUE;

-- Index for organization lookups
CREATE INDEX IF NOT EXISTS event_licenses_organization_id_idx
  ON public.event_licenses (organization_id);

-- ── 2. Quota trigger function ────────────────────────────────────────────────
-- SECURITY DEFINER is NOT used here: the trigger fires inside the service_role
-- write path; no privilege escalation is needed or wanted.
-- search_path is pinned to public to prevent search_path injection.

CREATE OR REPLACE FUNCTION public.check_free_event_quota()
RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = public
AS $$
DECLARE
  existing_count INT;
BEGIN
  IF NEW.is_free_quota = TRUE THEN
    SELECT COUNT(*) INTO existing_count
    FROM event_licenses
    WHERE user_id = NEW.user_id
      AND is_free_quota = TRUE
      AND date_trunc('month', created_at) = date_trunc('month', NOW());
    IF existing_count >= 2 THEN
      RAISE EXCEPTION 'quota_exhausted';
    END IF;
  END IF;
  RETURN NEW;
END;
$$;

-- ── 3. Attach trigger ────────────────────────────────────────────────────────
-- DROP IF EXISTS + CREATE makes the trigger attachment idempotent.

DROP TRIGGER IF EXISTS event_licenses_quota_check ON public.event_licenses;
CREATE TRIGGER event_licenses_quota_check
  BEFORE INSERT ON public.event_licenses
  FOR EACH ROW EXECUTE FUNCTION public.check_free_event_quota();

-- ── 4. RLS ───────────────────────────────────────────────────────────────────

ALTER TABLE public.event_licenses ENABLE ROW LEVEL SECURITY;

-- SELECT: authenticated users read only their own license rows
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'event_licenses' AND policyname = 'event_licenses_own_select'
  ) THEN
    CREATE POLICY "event_licenses_own_select" ON public.event_licenses
      FOR SELECT TO authenticated
      USING (user_id = auth.uid());
  END IF;
END $$;

-- No INSERT / UPDATE / DELETE policy for authenticated role.
-- Default deny covers all authenticated write attempts.
-- service_role writes bypass RLS; GRANT ALL below is belt-and-suspenders.

-- ── 5. Grants ────────────────────────────────────────────────────────────────

GRANT SELECT ON public.event_licenses TO authenticated;
GRANT ALL ON public.event_licenses TO service_role;
