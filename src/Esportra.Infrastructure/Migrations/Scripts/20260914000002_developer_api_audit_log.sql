-- ============================================================================
-- Migration: Developer API Audit Log
-- Purpose:   Append-only audit log for all developer API requests.
--            organization_id is denormalized (no join needed for analytics).
--
-- GDPR / PII NOTICE:
--   ip_address stores client IP addresses. This is personal data under GDPR
--   Article 4(1). A background cleanup job MUST be implemented to purge rows
--   older than 90 days (see PROJ-024 technical debt backlog).
--   Until that job exists, manual purge must run:
--     DELETE FROM developer_api_audit_log WHERE created_at < NOW() - INTERVAL '90 days';
--   authenticated users and service_role see ip_address — access must be
--   audited and restricted to the minimum necessary.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. Create developer_api_audit_log table
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.developer_api_audit_log (
    id                UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    api_key_id        UUID        NOT NULL REFERENCES public.developer_api_keys(id),
    organization_id   UUID        NOT NULL REFERENCES public.organizations(id),
    endpoint          TEXT        NOT NULL,
    method            TEXT        NOT NULL,
    response_status   INTEGER     NOT NULL,
    response_time_ms  INTEGER,
    rate_limited      BOOLEAN     NOT NULL DEFAULT false,
    ip_address        TEXT,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ---------------------------------------------------------------------------
-- 2. Indexes (optimised for the four primary analytics query patterns)
-- ---------------------------------------------------------------------------

-- Per-org summary queries (dashboard overview, billing aggregation)
CREATE INDEX IF NOT EXISTS idx_developer_api_audit_log_org_created
    ON public.developer_api_audit_log (organization_id, created_at DESC);

-- Endpoint breakdown queries (traffic analysis per key + path)
CREATE INDEX IF NOT EXISTS idx_developer_api_audit_log_key_endpoint_created
    ON public.developer_api_audit_log (api_key_id, endpoint, created_at DESC);

-- Error breakdown queries (error rate analysis per key + status)
CREATE INDEX IF NOT EXISTS idx_developer_api_audit_log_key_status_created
    ON public.developer_api_audit_log (api_key_id, response_status, created_at DESC);

-- Admin cross-partner queries (platform-wide time-range scans)
CREATE INDEX IF NOT EXISTS idx_developer_api_audit_log_created
    ON public.developer_api_audit_log (created_at DESC);

-- ---------------------------------------------------------------------------
-- 3. Row-Level Security (append-only for authenticated — no UPDATE/DELETE)
-- ---------------------------------------------------------------------------
ALTER TABLE public.developer_api_audit_log ENABLE ROW LEVEL SECURITY;

-- Org owners can read their own audit rows (defense-in-depth; app layer must
-- also filter by organization_id = @orgId on all Dapper queries).
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'developer_api_audit_log'
          AND policyname = 'developer_api_audit_log_owner_select'
    ) THEN
        CREATE POLICY developer_api_audit_log_owner_select
            ON public.developer_api_audit_log
            FOR SELECT
            TO authenticated
            USING (
                organization_id IN (
                    SELECT id FROM public.organizations WHERE owner_id = auth.uid()
                )
            );
    END IF;
END $$;

-- No INSERT/UPDATE/DELETE for authenticated users — append-only via service_role only.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'developer_api_audit_log'
          AND policyname = 'developer_api_audit_log_service_all'
    ) THEN
        CREATE POLICY developer_api_audit_log_service_all
            ON public.developer_api_audit_log
            FOR ALL
            TO service_role
            USING (true)
            WITH CHECK (true);
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- 4. GRANTs
-- ---------------------------------------------------------------------------
GRANT SELECT ON public.developer_api_audit_log TO authenticated;
GRANT ALL   ON public.developer_api_audit_log TO service_role;
