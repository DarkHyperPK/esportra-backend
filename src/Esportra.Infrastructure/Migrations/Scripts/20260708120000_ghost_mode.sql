-- Ghost Mode (User Impersonation) System
-- Secure read-only impersonation with JIT approval and full audit trail

-- JIT approval requests (non-super_admin must request approval)
CREATE TABLE IF NOT EXISTS ghost_approvals (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    requester_id UUID NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    target_user_id UUID NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    reason TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'approved', 'denied', 'expired')),
    approved_by UUID REFERENCES profiles(id) ON DELETE SET NULL,
    approved_at TIMESTAMPTZ,
    denial_reason TEXT,
    expires_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Ghost sessions with full tracking
CREATE TABLE IF NOT EXISTS ghost_sessions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    admin_id UUID NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    target_user_id UUID NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    approval_id UUID REFERENCES ghost_approvals(id) ON DELETE SET NULL,
    token_hash TEXT NOT NULL,
    reason TEXT NOT NULL,

    -- Security metadata
    admin_ip INET,
    admin_user_agent TEXT,

    -- Session tracking
    started_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at TIMESTAMPTZ NOT NULL,
    ended_at TIMESTAMPTZ,
    last_activity_at TIMESTAMPTZ,

    -- Audit
    pages_viewed JSONB NOT NULL DEFAULT '[]'::jsonb,
    fields_unmasked JSONB NOT NULL DEFAULT '[]'::jsonb,

    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Detailed data access log for compliance
CREATE TABLE IF NOT EXISTS ghost_data_access_log (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID NOT NULL REFERENCES ghost_sessions(id) ON DELETE CASCADE,
    field_path TEXT NOT NULL,
    was_unmasked BOOLEAN NOT NULL DEFAULT false,
    unmasked_reason TEXT,
    accessed_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Indexes
CREATE INDEX IF NOT EXISTS idx_ghost_approvals_requester ON ghost_approvals(requester_id);
CREATE INDEX IF NOT EXISTS idx_ghost_approvals_target ON ghost_approvals(target_user_id);
CREATE INDEX IF NOT EXISTS idx_ghost_approvals_status ON ghost_approvals(status) WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS idx_ghost_approvals_expires ON ghost_approvals(expires_at) WHERE status = 'approved';

CREATE INDEX IF NOT EXISTS idx_ghost_sessions_admin ON ghost_sessions(admin_id);
CREATE INDEX IF NOT EXISTS idx_ghost_sessions_target ON ghost_sessions(target_user_id);
CREATE INDEX IF NOT EXISTS idx_ghost_sessions_active ON ghost_sessions(admin_id) WHERE ended_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_ghost_sessions_expires ON ghost_sessions(expires_at) WHERE ended_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_ghost_data_access_session ON ghost_data_access_log(session_id);
CREATE INDEX IF NOT EXISTS idx_ghost_data_access_time ON ghost_data_access_log(accessed_at DESC);

-- RLS
ALTER TABLE ghost_approvals ENABLE ROW LEVEL SECURITY;
ALTER TABLE ghost_sessions ENABLE ROW LEVEL SECURITY;
ALTER TABLE ghost_data_access_log ENABLE ROW LEVEL SECURITY;

-- Service role full access
DROP POLICY IF EXISTS ghost_approvals_service ON ghost_approvals;
CREATE POLICY ghost_approvals_service ON ghost_approvals FOR ALL TO service_role USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS ghost_sessions_service ON ghost_sessions;
CREATE POLICY ghost_sessions_service ON ghost_sessions FOR ALL TO service_role USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS ghost_data_access_log_service ON ghost_data_access_log;
CREATE POLICY ghost_data_access_log_service ON ghost_data_access_log FOR ALL TO service_role USING (true) WITH CHECK (true);

-- Add permissions
INSERT INTO admin_permissions (id, key, name, description, category)
VALUES
    (gen_random_uuid(), 'users:impersonate', 'Ghost Mode', 'Impersonate users in read-only mode (requires approval for non-super_admin)', 'users'),
    (gen_random_uuid(), 'users:impersonate:full', 'Ghost Mode Full Access', 'View unmasked sensitive data during impersonation', 'users'),
    (gen_random_uuid(), 'users:impersonate:approve', 'Approve Ghost Requests', 'Approve or deny ghost mode requests from other admins', 'users'),
    (gen_random_uuid(), 'ghost:audit', 'View Ghost Audit', 'View ghost session history and data access logs', 'security')
ON CONFLICT (key) DO NOTHING;

-- Auto-expire pending approvals after 24 hours
CREATE OR REPLACE FUNCTION expire_ghost_approvals()
RETURNS void AS $$
BEGIN
    UPDATE ghost_approvals
    SET status = 'expired'
    WHERE status = 'pending'
      AND created_at < NOW() - INTERVAL '24 hours';

    UPDATE ghost_approvals
    SET status = 'expired'
    WHERE status = 'approved'
      AND expires_at IS NOT NULL
      AND expires_at < NOW();
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;

-- Auto-end expired ghost sessions
CREATE OR REPLACE FUNCTION end_expired_ghost_sessions()
RETURNS void AS $$
BEGIN
    UPDATE ghost_sessions
    SET ended_at = NOW()
    WHERE ended_at IS NULL
      AND expires_at < NOW();
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;
