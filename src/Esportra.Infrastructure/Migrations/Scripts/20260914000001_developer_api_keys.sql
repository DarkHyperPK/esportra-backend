-- ============================================================================
-- Migration: Developer API Keys Infrastructure
-- Purpose:   Adds is_api_approved flag to organizations (developer program gate)
--            and creates developer_api_keys table with:
--              - SHA-256 key hashing (key_hash stored, never plaintext)
--              - Key prefix for display identification
--              - Sandbox/live environment separation
--              - Status-based lifecycle (active → rotating → revoked)
--              - Grace period fields for key rotation
--              - Per-key rate limit configuration
--              - Scoped permissions via TEXT[]
--
-- Key limits (enforced at application layer, not DB constraint):
--              - Max 5 sandbox active keys per organization
--              - Max 3 live active keys per organization
--              - Max 2 live total active keys per organization
--              Reason: flexible thresholds per subscription tier without DDL.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. Add is_api_approved to organizations (developer program gate)
-- ---------------------------------------------------------------------------
ALTER TABLE public.organizations
    ADD COLUMN IF NOT EXISTS is_api_approved BOOLEAN NOT NULL DEFAULT false;

-- ---------------------------------------------------------------------------
-- 2. Create developer_api_keys table
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.developer_api_keys (
    id                  UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id     UUID          NOT NULL REFERENCES public.organizations(id) ON DELETE CASCADE,
    name                TEXT          NOT NULL,
    key_hash            TEXT          NOT NULL UNIQUE,
    key_prefix          TEXT          NOT NULL,
    environment         TEXT          NOT NULL DEFAULT 'sandbox',
    status              TEXT          NOT NULL DEFAULT 'active',
    scopes              TEXT[]        NOT NULL DEFAULT '{}',
    rate_limit_per_min  INTEGER       NOT NULL DEFAULT 60,
    rotated_to          UUID          REFERENCES public.developer_api_keys(id),
    grace_period_until  TIMESTAMPTZ,
    last_used_at        TIMESTAMPTZ,
    created_by          UUID          REFERENCES auth.users(id),
    created_at          TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    revoked_at          TIMESTAMPTZ
);

-- ---------------------------------------------------------------------------
-- 3. Constraints (no IF NOT EXISTS on ADD CONSTRAINT — wrapped in DO $$ guard)
-- ---------------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'developer_api_keys_environment_check'
    ) THEN
        ALTER TABLE public.developer_api_keys
            ADD CONSTRAINT developer_api_keys_environment_check
            CHECK (environment IN ('sandbox', 'live'));
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'developer_api_keys_status_check'
    ) THEN
        ALTER TABLE public.developer_api_keys
            ADD CONSTRAINT developer_api_keys_status_check
            CHECK (status IN ('active', 'rotating', 'revoked'));
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- 4. Indexes
-- ---------------------------------------------------------------------------

-- Self-serve listing: fetch keys for an org ordered by most recent
CREATE INDEX IF NOT EXISTS idx_developer_api_keys_org_status_created
    ON public.developer_api_keys (organization_id, status, created_at DESC);

-- Active key count enforcement: count active keys per org/environment
CREATE INDEX IF NOT EXISTS idx_developer_api_keys_org_env_status
    ON public.developer_api_keys (organization_id, environment, status);

-- ---------------------------------------------------------------------------
-- 5. Row-Level Security
-- ---------------------------------------------------------------------------
ALTER TABLE public.developer_api_keys ENABLE ROW LEVEL SECURITY;

-- Org owners can read their own keys (defense-in-depth; app layer must also
-- filter by organization_id = @orgId on all Dapper queries, since
-- service_role bypasses RLS).
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'developer_api_keys'
          AND policyname = 'developer_api_keys_owner_select'
    ) THEN
        CREATE POLICY developer_api_keys_owner_select
            ON public.developer_api_keys
            FOR SELECT
            TO authenticated
            USING (
                organization_id IN (
                    SELECT id FROM public.organizations WHERE owner_id = auth.uid()
                )
            );
    END IF;
END $$;

-- INSERT/UPDATE/DELETE only via service_role (API middleware and admin run as service_role)
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'developer_api_keys'
          AND policyname = 'developer_api_keys_service_all'
    ) THEN
        CREATE POLICY developer_api_keys_service_all
            ON public.developer_api_keys
            FOR ALL
            TO service_role
            USING (true)
            WITH CHECK (true);
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- 6. GRANTs
-- ---------------------------------------------------------------------------
GRANT SELECT ON public.developer_api_keys TO authenticated;
GRANT ALL   ON public.developer_api_keys TO service_role;
