-- Indexes to support admin session management queries

-- Index for filtering audit_logs by action_type (login/logout session audit)
CREATE INDEX IF NOT EXISTS idx_audit_logs_action_type_created
ON audit_logs (action_type, created_at DESC);

-- Index for online-count query (recent activity window)
CREATE INDEX IF NOT EXISTS idx_audit_logs_created_at_admin
ON audit_logs (created_at DESC)
WHERE admin_id IS NOT NULL;
