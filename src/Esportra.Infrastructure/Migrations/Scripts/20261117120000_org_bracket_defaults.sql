-- ============================================================================
-- PROJ-078: Organization-level bracket defaults
-- ============================================================================
--
-- Context:
--   Tournament creation hardcodes a 32-team cap. The update endpoint already
--   respects the per-tournament license dynamically, but creation always
--   reserves a "free" license with bracket_max_teams = 32.
--
--   This migration adds a default bracket limit to organizations so admins
--   can pre-authorize an org for higher limits (including unlimited).
--   Solo organizers without an org fall back to the platform default (32)
--   in application code.
--
-- Idempotent: safe to replay. Uses IF NOT EXISTS throughout.
-- ============================================================================

-- == 1. Add default_bracket_max_teams column ==================================
-- 0 = unlimited; 32 = free tier default.

ALTER TABLE public.organizations
  ADD COLUMN IF NOT EXISTS default_bracket_max_teams INT NOT NULL DEFAULT 32;

-- == 2. RLS fix: extend protected columns to include default_bracket_max_teams =
-- [CIO CRITICAL]
--
-- The PROJ-077 policy already blocks org owners from changing
-- monthly_event_quota, subscription_tier, and is_verified.
-- This recreate adds default_bracket_max_teams to the blocked set.

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
    AND default_bracket_max_teams = (SELECT o.default_bracket_max_teams FROM public.organizations o WHERE o.id = organizations.id)
  );
