-- Broadcast Notifications System
-- Modular channel-agnostic system, starting with in-app

-- Broadcast templates for reusable messages
CREATE TABLE IF NOT EXISTS broadcast_templates (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name TEXT NOT NULL,
    title TEXT NOT NULL,
    content TEXT NOT NULL,
    content_html TEXT,
    broadcast_type TEXT NOT NULL DEFAULT 'announcement',
    created_by UUID REFERENCES profiles(id) ON DELETE SET NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Broadcast definitions
CREATE TABLE IF NOT EXISTS broadcasts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    title TEXT NOT NULL,
    content TEXT NOT NULL,
    content_html TEXT,
    broadcast_type TEXT NOT NULL DEFAULT 'announcement',
    priority TEXT NOT NULL DEFAULT 'normal' CHECK (priority IN ('low', 'normal', 'high', 'urgent')),

    -- Targeting
    target_type TEXT NOT NULL DEFAULT 'all' CHECK (target_type IN ('all', 'segment', 'users')),
    target_segment JSONB,
    target_user_ids UUID[],

    -- Channels (modular)
    channels TEXT[] NOT NULL DEFAULT '{in_app}',

    -- Scheduling
    status TEXT NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'scheduled', 'sending', 'sent', 'cancelled')),
    scheduled_at TIMESTAMPTZ,
    sent_at TIMESTAMPTZ,

    -- Stats
    total_recipients INT NOT NULL DEFAULT 0,
    delivered_count INT NOT NULL DEFAULT 0,
    read_count INT NOT NULL DEFAULT 0,

    -- Meta
    created_by UUID REFERENCES profiles(id) ON DELETE SET NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Delivery tracking per user per channel
CREATE TABLE IF NOT EXISTS broadcast_deliveries (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    broadcast_id UUID NOT NULL REFERENCES broadcasts(id) ON DELETE CASCADE,
    user_id UUID NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    channel TEXT NOT NULL DEFAULT 'in_app',
    status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'delivered', 'read', 'failed')),
    delivered_at TIMESTAMPTZ,
    read_at TIMESTAMPTZ,
    error_message TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE(broadcast_id, user_id, channel)
);

-- Indexes
CREATE INDEX IF NOT EXISTS idx_broadcasts_status ON broadcasts(status);
CREATE INDEX IF NOT EXISTS idx_broadcasts_scheduled ON broadcasts(scheduled_at) WHERE status = 'scheduled';
CREATE INDEX IF NOT EXISTS idx_broadcasts_created ON broadcasts(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_broadcast_deliveries_user ON broadcast_deliveries(user_id, status);
CREATE INDEX IF NOT EXISTS idx_broadcast_deliveries_broadcast ON broadcast_deliveries(broadcast_id);
CREATE INDEX IF NOT EXISTS idx_broadcast_deliveries_pending ON broadcast_deliveries(broadcast_id) WHERE status = 'pending';

-- RLS
ALTER TABLE broadcast_templates ENABLE ROW LEVEL SECURITY;
ALTER TABLE broadcasts ENABLE ROW LEVEL SECURITY;
ALTER TABLE broadcast_deliveries ENABLE ROW LEVEL SECURITY;

-- Service role full access
DROP POLICY IF EXISTS broadcast_templates_service ON broadcast_templates;
CREATE POLICY broadcast_templates_service ON broadcast_templates FOR ALL TO service_role USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS broadcasts_service ON broadcasts;
CREATE POLICY broadcasts_service ON broadcasts FOR ALL TO service_role USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS broadcast_deliveries_service ON broadcast_deliveries;
CREATE POLICY broadcast_deliveries_service ON broadcast_deliveries FOR ALL TO service_role USING (true) WITH CHECK (true);

-- Users can read their own deliveries
DROP POLICY IF EXISTS broadcast_deliveries_user_read ON broadcast_deliveries;
CREATE POLICY broadcast_deliveries_user_read ON broadcast_deliveries
    FOR SELECT TO authenticated
    USING (user_id = auth.uid());

-- Users can update their own deliveries (mark as read)
DROP POLICY IF EXISTS broadcast_deliveries_user_update ON broadcast_deliveries;
CREATE POLICY broadcast_deliveries_user_update ON broadcast_deliveries
    FOR UPDATE TO authenticated
    USING (user_id = auth.uid())
    WITH CHECK (user_id = auth.uid());

-- Add permissions
INSERT INTO admin_permissions (id, key, name, description, category)
VALUES
    (gen_random_uuid(), 'broadcasts:view', 'View Broadcasts', 'View broadcast messages and stats', 'operations'),
    (gen_random_uuid(), 'broadcasts:create', 'Create Broadcasts', 'Create and schedule broadcast messages', 'operations'),
    (gen_random_uuid(), 'broadcasts:edit', 'Edit Broadcasts', 'Modify draft broadcasts', 'operations'),
    (gen_random_uuid(), 'broadcasts:delete', 'Delete Broadcasts', 'Delete draft broadcasts', 'operations'),
    (gen_random_uuid(), 'broadcasts:send', 'Send Broadcasts', 'Send broadcast messages immediately', 'operations')
ON CONFLICT (key) DO NOTHING;
