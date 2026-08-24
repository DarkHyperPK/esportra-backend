-- Audit unification (Admin Centre V2 — audit console):
-- Capture actor IP + user agent + severity on BOTH audit streams so the unified
-- audit console has complete attribution. Idempotent.

ALTER TABLE staff_audit_log ADD COLUMN IF NOT EXISTS ip_address INET;
ALTER TABLE staff_audit_log ADD COLUMN IF NOT EXISTS user_agent TEXT;
ALTER TABLE staff_audit_log ADD COLUMN IF NOT EXISTS severity TEXT NOT NULL DEFAULT 'info';
ALTER TABLE staff_audit_log ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT now();

ALTER TABLE audit_logs ADD COLUMN IF NOT EXISTS ip_address INET;
ALTER TABLE audit_logs ADD COLUMN IF NOT EXISTS user_agent TEXT;

CREATE INDEX IF NOT EXISTS idx_staff_audit_log_created_at ON staff_audit_log (created_at DESC);
CREATE INDEX IF NOT EXISTS idx_staff_audit_log_action ON staff_audit_log (action);
CREATE INDEX IF NOT EXISTS idx_audit_logs_created_at ON audit_logs (created_at DESC);
