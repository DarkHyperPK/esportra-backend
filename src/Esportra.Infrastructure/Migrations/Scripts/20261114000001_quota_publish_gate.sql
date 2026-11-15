-- ============================================================================
-- PROJ-077: Move monthly event quota gate from creation to publishing
-- ============================================================================
--
-- Context:
--   Organizers can now draft unlimited tournaments but can only publish up to
--   2 free per calendar month (or a custom per-org limit set by admins).
--   The quota check moves from a Postgres BEFORE INSERT trigger on
--   event_licenses to application-level enforcement at publish time.
--
-- This migration:
--   1. Adds monthly_event_quota (nullable INT) to organizations.
--      NULL = default 2 (application handles COALESCE).
--   2. Drops the event_licenses_quota_check trigger (no longer needed).
--   3. Drops the check_free_event_quota() function (cleanup).
--   4. [CIO CRITICAL] Recreates organizations_owner_update RLS policy so
--      org owners cannot change monthly_event_quota, subscription_tier,
--      or is_verified via PostgREST.
--
-- Idempotent: safe to replay. Uses IF NOT EXISTS / IF EXISTS throughout.
-- ============================================================================

-- == 1. Add monthly_event_quota column ========================================

ALTER TABLE public.organizations
  ADD COLUMN IF NOT EXISTS monthly_event_quota INT;

-- No CHECK constraint: admin can set any positive integer; NULL = default 2.

-- == 2. Drop quota trigger (gate moves to publish endpoint) ===================

DROP TRIGGER IF EXISTS event_licenses_quota_check ON public.event_licenses;

-- == 3. Drop trigger function (cleanup) =======================================

DROP FUNCTION IF EXISTS public.check_free_event_quota();

-- == 4. RLS fix: restrict owner UPDATE on protected columns ===================
-- [CIO CRITICAL]
--
-- The previous policy allowed org owners to UPDATE any column (including
-- subscription_tier, is_verified, and now monthly_event_quota) via PostgREST.
-- The new WITH CHECK ensures those three columns are unchanged by comparing
-- each against its current value via a correlated subquery.
--
-- COALESCE(-1) handles the nullable monthly_event_quota correctly:
-- NULL = NULL is unknown in SQL, but COALESCE(NULL,-1) = COALESCE(NULL,-1)
-- evaluates to TRUE.

DROP POLICY IF EXISTS "organizations_owner_update" ON public.organizations;

CREATE POLICY "organizations_owner_update" ON public.organizations
  FOR UPDATE
  USING (auth.uid() = owner_id)
  WITH CHECK (
    auth.uid() = owner_id
    AND COALESCE(monthly_event_quota, -1) = COALESCE(
          (SELECT o.monthly_event_quota FROM public.organizations o WHERE o.id = organizations.id), -1)
    AND subscription_tier = (SELECT o.subscription_tier FROM public.organizations o WHERE o.id = organizations.id)
    AND is_verified = (SELECT o.is_verified FROM public.organizations o WHERE o.id = organizations.id)
  );
