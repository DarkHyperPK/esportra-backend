-- Durable cleanup retries for server-owned sponsor creative assets.
CREATE TABLE IF NOT EXISTS public.sponsor_asset_cleanup_jobs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    bucket TEXT NOT NULL,
    object_path TEXT NOT NULL,
    attempts INTEGER NOT NULL DEFAULT 0,
    next_attempt_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_error TEXT,
    completed_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    failed_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS sponsor_asset_cleanup_pending_idx
    ON public.sponsor_asset_cleanup_jobs (next_attempt_at)
    WHERE completed_at IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS sponsor_asset_cleanup_pending_unique_idx
    ON public.sponsor_asset_cleanup_jobs (bucket, object_path)
    WHERE completed_at IS NULL AND failed_at IS NULL;

ALTER TABLE public.sponsor_asset_cleanup_jobs ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.sponsor_asset_cleanup_jobs FORCE ROW LEVEL SECURITY;
REVOKE ALL ON public.sponsor_asset_cleanup_jobs FROM anon, authenticated;
DROP POLICY IF EXISTS sponsor_asset_cleanup_service_role_all ON public.sponsor_asset_cleanup_jobs;
CREATE POLICY sponsor_asset_cleanup_service_role_all ON public.sponsor_asset_cleanup_jobs
    FOR ALL TO service_role USING (true) WITH CHECK (true);
GRANT ALL ON public.sponsor_asset_cleanup_jobs TO service_role;
