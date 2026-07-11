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

CREATE INDEX IF NOT EXISTS idx_revoked_sessions_user_active
    ON public.revoked_sessions(user_id)
    WHERE expires_at > NOW();

CREATE INDEX IF NOT EXISTS idx_revoked_sessions_expires
    ON public.revoked_sessions(expires_at);

-- RLS policies
ALTER TABLE public.revoked_sessions ENABLE ROW LEVEL SECURITY;

-- Only admins can view revoked sessions
CREATE POLICY "Admins can view revoked sessions" ON public.revoked_sessions
    FOR SELECT
    TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM user_roles ur
            WHERE ur.user_id = auth.uid()
            AND ur.role IN ('Super Admin', 'Admin')
            AND ur.is_active = true
        )
    );

-- Only admins can insert revoked sessions (via service role in API)
CREATE POLICY "Service role can manage revoked sessions" ON public.revoked_sessions
    FOR ALL
    TO service_role
    USING (true)
    WITH CHECK (true);

-- Cleanup job trigger (optional - can be run via scheduled job instead)
CREATE OR REPLACE FUNCTION cleanup_expired_revoked_sessions()
RETURNS void AS $$
BEGIN
    DELETE FROM public.revoked_sessions WHERE expires_at < NOW() - INTERVAL '1 day';
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;
