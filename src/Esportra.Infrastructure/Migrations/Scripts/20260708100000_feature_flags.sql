-- Feature Flags System
-- Supports: global flags, segment rules, percentage rollouts, user overrides

-- Feature flag definitions
CREATE TABLE IF NOT EXISTS feature_flags (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    key TEXT UNIQUE NOT NULL,
    name TEXT NOT NULL,
    description TEXT,
    flag_type TEXT NOT NULL DEFAULT 'boolean',
    default_value JSONB NOT NULL DEFAULT '{"enabled": false}'::jsonb,
    is_enabled BOOLEAN NOT NULL DEFAULT true,
    created_by UUID REFERENCES profiles(id) ON DELETE SET NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Segment-based rules for targeting
CREATE TABLE IF NOT EXISTS feature_flag_rules (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    flag_id UUID NOT NULL REFERENCES feature_flags(id) ON DELETE CASCADE,
    priority INT NOT NULL DEFAULT 0,
    conditions JSONB NOT NULL DEFAULT '{}'::jsonb,
    value JSONB NOT NULL,
    percentage INT CHECK (percentage IS NULL OR (percentage >= 0 AND percentage <= 100)),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- User-specific overrides
CREATE TABLE IF NOT EXISTS feature_flag_overrides (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    flag_id UUID NOT NULL REFERENCES feature_flags(id) ON DELETE CASCADE,
    user_id UUID NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    value JSONB NOT NULL,
    reason TEXT,
    created_by UUID REFERENCES profiles(id) ON DELETE SET NULL,
    expires_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE(flag_id, user_id)
);

-- Indexes
CREATE INDEX IF NOT EXISTS idx_feature_flags_key ON feature_flags(key);
CREATE INDEX IF NOT EXISTS idx_feature_flags_enabled ON feature_flags(is_enabled) WHERE is_enabled = true;
CREATE INDEX IF NOT EXISTS idx_feature_flag_rules_flag ON feature_flag_rules(flag_id);
CREATE INDEX IF NOT EXISTS idx_feature_flag_rules_priority ON feature_flag_rules(flag_id, priority DESC);
CREATE INDEX IF NOT EXISTS idx_feature_flag_overrides_user ON feature_flag_overrides(user_id);
CREATE INDEX IF NOT EXISTS idx_feature_flag_overrides_flag ON feature_flag_overrides(flag_id);

-- RLS
ALTER TABLE feature_flags ENABLE ROW LEVEL SECURITY;
ALTER TABLE feature_flag_rules ENABLE ROW LEVEL SECURITY;
ALTER TABLE feature_flag_overrides ENABLE ROW LEVEL SECURITY;

-- Service role full access
DROP POLICY IF EXISTS feature_flags_service ON feature_flags;
CREATE POLICY feature_flags_service ON feature_flags FOR ALL TO service_role USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS feature_flag_rules_service ON feature_flag_rules;
CREATE POLICY feature_flag_rules_service ON feature_flag_rules FOR ALL TO service_role USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS feature_flag_overrides_service ON feature_flag_overrides;
CREATE POLICY feature_flag_overrides_service ON feature_flag_overrides FOR ALL TO service_role USING (true) WITH CHECK (true);

-- Authenticated users can read flags (for client-side evaluation)
DROP POLICY IF EXISTS feature_flags_read ON feature_flags;
CREATE POLICY feature_flags_read ON feature_flags FOR SELECT TO authenticated USING (is_enabled = true);

-- Add permissions for feature flag management
INSERT INTO admin_permissions (id, key, name, description, category)
VALUES
    (gen_random_uuid(), 'feature_flags:view', 'View Feature Flags', 'View feature flags and their configuration', 'system'),
    (gen_random_uuid(), 'feature_flags:create', 'Create Feature Flags', 'Create new feature flags', 'system'),
    (gen_random_uuid(), 'feature_flags:edit', 'Edit Feature Flags', 'Modify existing feature flags and rules', 'system'),
    (gen_random_uuid(), 'feature_flags:delete', 'Delete Feature Flags', 'Delete feature flags', 'system')
ON CONFLICT (key) DO NOTHING;
