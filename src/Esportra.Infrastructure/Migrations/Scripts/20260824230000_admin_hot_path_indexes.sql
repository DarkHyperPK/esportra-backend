-- Performance indexes for admin hot paths (perf audit follow-up).

-- Command-centre 14-day series + users-list sort currently scan profiles.
CREATE INDEX IF NOT EXISTS idx_profiles_created_at ON public.profiles (created_at DESC);

-- Audit console filter patterns (severity/target slices over time-ordered scans).
CREATE INDEX IF NOT EXISTS idx_audit_logs_created_sev_type
    ON public.audit_logs (created_at DESC, severity, target_type);
CREATE INDEX IF NOT EXISTS idx_staff_audit_created_sev_type
    ON public.staff_audit_log (created_at DESC, severity, target_type);
