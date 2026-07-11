-- Session revocation table for server-side session blacklisting
-- Allows immediate session termination without waiting for JWT expiry

CREATE TABLE IF NOT EXISTS public.revoked_sessions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    revoked_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    revoked_by UUID NOT NULL REFERENCES auth.users(id),
    reason TEXT,
    expires_at TIMESTAMPTZ NOT NULL,
    UNIQUE (user_id, revoked_at)
);

COMMENT ON TABLE public.revoked_sessions IS 'Tracks revoked sessions for server-side session blacklisting';

-- Composite index for efficient user lookup with expiry filtering
-- Query: SELECT ... WHERE user_id = $1 AND expires_at > NOW()
CREATE INDEX IF NOT EXISTS idx_revoked_sessions_user_expires
    ON public.revoked_sessions(user_id, expires_at DESC);

-- Index for cleanup job that deletes expired entries
CREATE INDEX IF NOT EXISTS idx_revoked_sessions_expires
    ON public.revoked_sessions(expires_at);

-- RLS policies
ALTER TABLE public.revoked_sessions ENABLE ROW LEVEL SECURITY;

-- Drop existing policies for idempotency
DROP POLICY IF EXISTS "revoked_sessions_admin_select" ON public.revoked_sessions;
DROP POLICY IF EXISTS "revoked_sessions_service_role_all" ON public.revoked_sessions;

-- Only admins can view revoked sessions (uses admin_user_roles + admin_roles pattern)
-- Note: No is_active check - row existence in admin_user_roles means role is active
CREATE POLICY "revoked_sessions_admin_select" ON public.revoked_sessions
    FOR SELECT
    TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.admin_user_roles aur
            JOIN public.admin_roles ar ON ar.id = aur.role_id
            WHERE aur.user_id = auth.uid()
            AND ar.name IN ('super_admin', 'ops_admin', 'support_admin')
        )
    );

-- Service role can manage all revoked sessions (API uses service_role key)
CREATE POLICY "revoked_sessions_service_role_all" ON public.revoked_sessions
    FOR ALL
    TO service_role
    USING (true)
    WITH CHECK (true);

-- Grant table access
GRANT SELECT ON public.revoked_sessions TO authenticated;
GRANT ALL ON public.revoked_sessions TO service_role;

-- Cleanup function for expired revocations (can be called by scheduled job)
CREATE OR REPLACE FUNCTION cleanup_expired_revoked_sessions()
RETURNS void AS $$
BEGIN
    DELETE FROM public.revoked_sessions WHERE expires_at < NOW() - INTERVAL '1 day';
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;
