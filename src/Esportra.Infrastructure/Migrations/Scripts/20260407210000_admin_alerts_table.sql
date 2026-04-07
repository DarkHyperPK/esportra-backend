-- Admin Alerts: operational alerts for admin dashboard
CREATE TABLE IF NOT EXISTS admin_alerts (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    type            TEXT NOT NULL,           -- dispute_filed, tournament_pending, registration_spike, kyc_pending, system_event
    severity        TEXT NOT NULL DEFAULT 'info', -- info, warning, critical
    title           TEXT NOT NULL,
    message         TEXT,
    data            JSONB DEFAULT '{}',
    status          TEXT NOT NULL DEFAULT 'active', -- active, acknowledged, resolved
    acknowledged_by UUID REFERENCES auth.users(id),
    acknowledged_at TIMESTAMPTZ,
    resolved_by     UUID REFERENCES auth.users(id),
    resolved_at     TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

ALTER TABLE admin_alerts ENABLE ROW LEVEL SECURITY;

-- RLS: only authenticated users can read (admin check done in app layer)
DO $$ BEGIN
IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'admin_alerts' AND policyname = 'admin_alerts_service_role_all') THEN
    CREATE POLICY admin_alerts_service_role_all ON admin_alerts FOR ALL TO service_role USING (true) WITH CHECK (true);
END IF;
END $$;

DO $$ BEGIN
IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'admin_alerts' AND policyname = 'admin_alerts_authenticated_read') THEN
    CREATE POLICY admin_alerts_authenticated_read ON admin_alerts FOR SELECT TO authenticated USING (true);
END IF;
END $$;

-- Grants
GRANT ALL ON admin_alerts TO service_role;
GRANT SELECT ON admin_alerts TO authenticated;

-- Index for efficient queries
CREATE INDEX IF NOT EXISTS idx_admin_alerts_status_created ON admin_alerts (status, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_admin_alerts_type ON admin_alerts (type);
CREATE INDEX IF NOT EXISTS idx_admin_alerts_severity ON admin_alerts (severity);
