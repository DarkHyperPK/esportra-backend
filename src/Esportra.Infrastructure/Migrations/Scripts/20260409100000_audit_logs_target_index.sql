-- Add composite index on audit_logs for entity history queries
CREATE INDEX IF NOT EXISTS idx_audit_logs_target
ON audit_logs (target_type, target_id, created_at DESC);
