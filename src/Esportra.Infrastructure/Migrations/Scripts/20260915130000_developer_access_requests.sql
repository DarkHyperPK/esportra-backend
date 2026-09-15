-- ============================================================================
-- Migration: Developer Access Requests
-- Purpose:   Stores live API access applications from Tournament Organizers.
--
--            Flow:
--            1. TO submits application (organization_id, requested_by, intended_use)
--            2. Admin reviews and approves/rejects (admin_notes, reviewed_by, reviewed_at)
--            3. On approval, organization.is_api_approved set to true (app layer)
--
--            Business rule (enforced at application layer, not DB):
--            - One pending application per organization at a time
--              (checked before INSERT; admin must approve/reject before re-applying)
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. Create developer_access_requests table
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.developer_access_requests (
    id                  UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id     UUID          NOT NULL REFERENCES public.organizations(id) ON DELETE CASCADE,
    requested_by        UUID          NOT NULL REFERENCES auth.users(id),
    intended_use        TEXT          NOT NULL,
    status              TEXT          NOT NULL DEFAULT 'pending',
    admin_notes         TEXT,
    reviewed_by         UUID          REFERENCES auth.users(id),
    created_at          TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    reviewed_at         TIMESTAMPTZ
);

-- ---------------------------------------------------------------------------
-- 2. Constraints (no IF NOT EXISTS on ADD CONSTRAINT — wrapped in DO $$ guard)
-- ---------------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'developer_access_requests_status_check'
    ) THEN
        ALTER TABLE public.developer_access_requests
            ADD CONSTRAINT developer_access_requests_status_check
            CHECK (status IN ('pending', 'approved', 'rejected'));
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- 3. Indexes
-- ---------------------------------------------------------------------------

-- Check one-pending-per-org: WHERE organization_id = @orgId AND status = 'pending'
CREATE INDEX IF NOT EXISTS idx_developer_access_requests_org_status
    ON public.developer_access_requests (organization_id, status);

-- Admin listing by status: ORDER BY created_at DESC WHERE status = 'pending'
CREATE INDEX IF NOT EXISTS idx_developer_access_requests_status_created
    ON public.developer_access_requests (status, created_at DESC);

-- ---------------------------------------------------------------------------
-- 4. Row-Level Security
-- ---------------------------------------------------------------------------
ALTER TABLE public.developer_access_requests ENABLE ROW LEVEL SECURITY;

-- Org owners can read their own access requests (defense-in-depth; app layer
-- must also filter by organization_id = @orgId on all Dapper queries, since
-- service_role bypasses RLS).
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'developer_access_requests'
          AND policyname = 'developer_access_requests_owner_select'
    ) THEN
        CREATE POLICY developer_access_requests_owner_select
            ON public.developer_access_requests
            FOR SELECT
            TO authenticated
            USING (
                organization_id IN (
                    SELECT id FROM public.organizations WHERE owner_id = auth.uid()
                )
            );
    END IF;
END $$;

-- INSERT/UPDATE/DELETE only via service_role (API endpoints and admin run as service_role)
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'developer_access_requests'
          AND policyname = 'developer_access_requests_service_all'
    ) THEN
        CREATE POLICY developer_access_requests_service_all
            ON public.developer_access_requests
            FOR ALL
            TO service_role
            USING (true)
            WITH CHECK (true);
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- 5. GRANTs
-- ---------------------------------------------------------------------------
GRANT SELECT ON public.developer_access_requests TO authenticated;
GRANT ALL   ON public.developer_access_requests TO service_role;
