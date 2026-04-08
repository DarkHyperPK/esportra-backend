-- Phase 14: Anomaly Detection
-- Tables for anomaly detection rules and triggered events.

-- ── anomaly_rules ─────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS anomaly_rules (
    id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    name                TEXT        NOT NULL,
    description         TEXT        NOT NULL DEFAULT '',
    metric              TEXT        NOT NULL,   -- 'failed_logins', 'registrations', 'new_accounts', 'reports', 'disputes'
    threshold_count     INT         NOT NULL,   -- trigger if count exceeds this in the window
    window_minutes      INT         NOT NULL,   -- time window to count events in
    severity            TEXT        NOT NULL DEFAULT 'medium'
                            CHECK (severity IN ('low', 'medium', 'high', 'critical')),
    is_active           BOOLEAN     NOT NULL DEFAULT TRUE,
    last_triggered_at   TIMESTAMPTZ,
    cooldown_minutes    INT         NOT NULL DEFAULT 60,  -- don't re-fire within this period
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ── anomaly_events ────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS anomaly_events (
    id                UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    rule_id           UUID        NOT NULL REFERENCES anomaly_rules(id) ON DELETE CASCADE,
    metric            TEXT        NOT NULL,
    count_observed    INT         NOT NULL,
    threshold_count   INT         NOT NULL,
    window_minutes    INT         NOT NULL,
    detected_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    resolved_at       TIMESTAMPTZ,
    resolved_by       UUID        REFERENCES auth.users(id),
    is_resolved       BOOLEAN     NOT NULL DEFAULT FALSE,
    details           JSONB       NOT NULL DEFAULT '{}'
);

-- ── Indexes ───────────────────────────────────────────────────────────────────
CREATE INDEX IF NOT EXISTS idx_anomaly_events_rule_id
    ON anomaly_events (rule_id, detected_at DESC);

CREATE INDEX IF NOT EXISTS idx_anomaly_events_is_resolved
    ON anomaly_events (is_resolved, detected_at DESC);

CREATE INDEX IF NOT EXISTS idx_anomaly_rules_metric_active
    ON anomaly_rules (metric, is_active);

-- ── RLS ───────────────────────────────────────────────────────────────────────
ALTER TABLE anomaly_rules  ENABLE ROW LEVEL SECURITY;
ALTER TABLE anomaly_events ENABLE ROW LEVEL SECURITY;

-- anomaly_rules: service_role full access, admin users read
DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'anomaly_rules' AND policyname = 'anomaly_rules_service_role_all'
    ) THEN
        CREATE POLICY anomaly_rules_service_role_all ON anomaly_rules
            FOR ALL TO service_role USING (true) WITH CHECK (true);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'anomaly_rules' AND policyname = 'anomaly_rules_admin_read'
    ) THEN
        CREATE POLICY anomaly_rules_admin_read ON anomaly_rules
            FOR SELECT TO authenticated
            USING (EXISTS (SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid()));
    END IF;
END $$;

-- anomaly_events: service_role full access, admin users read
DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'anomaly_events' AND policyname = 'anomaly_events_service_role_all'
    ) THEN
        CREATE POLICY anomaly_events_service_role_all ON anomaly_events
            FOR ALL TO service_role USING (true) WITH CHECK (true);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'anomaly_events' AND policyname = 'anomaly_events_admin_read'
    ) THEN
        CREATE POLICY anomaly_events_admin_read ON anomaly_events
            FOR SELECT TO authenticated
            USING (EXISTS (SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid()));
    END IF;
END $$;

-- ── Grants ────────────────────────────────────────────────────────────────────
GRANT ALL    ON anomaly_rules  TO service_role;
GRANT SELECT ON anomaly_rules  TO authenticated;
GRANT ALL    ON anomaly_events TO service_role;
GRANT SELECT ON anomaly_events TO authenticated;

-- ── Seed default rules ────────────────────────────────────────────────────────
INSERT INTO anomaly_rules (name, description, metric, threshold_count, window_minutes, severity, cooldown_minutes)
VALUES
    (
        'Failed Login Spike',
        'Triggers when failed login attempts exceed threshold within the time window — possible brute-force or credential-stuffing attack.',
        'failed_logins',
        10, 5, 'high', 60
    ),
    (
        'Mass Account Creation',
        'Triggers when a large number of new accounts are created within the time window — possible bot registration.',
        'new_accounts',
        20, 10, 'medium', 60
    ),
    (
        'Report Surge',
        'Triggers when moderation reports spike — possible coordinated abuse.',
        'reports',
        15, 30, 'medium', 120
    ),
    (
        'Dispute Surge',
        'Triggers when match disputes spike — possible collusion or match-fixing.',
        'disputes',
        10, 15, 'medium', 60
    ),
    (
        'Registration Spike',
        'Triggers when tournament registrations surge — possible fake team registrations.',
        'registrations',
        50, 10, 'low', 30
    )
ON CONFLICT DO NOTHING;
