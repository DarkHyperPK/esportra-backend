-- ============================================================================
-- Migration: IP Allowlist for Admin Panel
-- Adds admin_ip_allowlist table + system_settings entry for toggling
-- ============================================================================

-- ── Table ──────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS admin_ip_allowlist (
    id          UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    ip_address  TEXT        NOT NULL,
    label       TEXT        NOT NULL DEFAULT '',
    created_by  UUID        REFERENCES auth.users(id),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at  TIMESTAMPTZ,
    is_active   BOOLEAN     NOT NULL DEFAULT TRUE,
    CONSTRAINT  uq_ip_allowlist_address UNIQUE (ip_address)
);

-- ── RLS ────────────────────────────────────────────────────────────────────
ALTER TABLE admin_ip_allowlist ENABLE ROW LEVEL SECURITY;

-- Admin-only SELECT: user must have an admin role
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'admin_ip_allowlist_select' AND tablename = 'admin_ip_allowlist'
  ) THEN
    CREATE POLICY "admin_ip_allowlist_select" ON admin_ip_allowlist
      FOR SELECT TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid()
        )
      );
  END IF;
END; $$;

-- Admin-only INSERT: user must have an admin role
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'admin_ip_allowlist_insert' AND tablename = 'admin_ip_allowlist'
  ) THEN
    CREATE POLICY "admin_ip_allowlist_insert" ON admin_ip_allowlist
      FOR INSERT TO authenticated
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid()
        )
      );
  END IF;
END; $$;

-- Admin-only UPDATE: user must have an admin role
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'admin_ip_allowlist_update' AND tablename = 'admin_ip_allowlist'
  ) THEN
    CREATE POLICY "admin_ip_allowlist_update" ON admin_ip_allowlist
      FOR UPDATE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid()
        )
      );
  END IF;
END; $$;

-- Admin-only DELETE: user must have an admin role
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'admin_ip_allowlist_delete' AND tablename = 'admin_ip_allowlist'
  ) THEN
    CREATE POLICY "admin_ip_allowlist_delete" ON admin_ip_allowlist
      FOR DELETE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid()
        )
      );
  END IF;
END; $$;

-- ── Grants ─────────────────────────────────────────────────────────────────
GRANT SELECT ON admin_ip_allowlist TO authenticated;
GRANT ALL    ON admin_ip_allowlist TO service_role;

-- ── Indexes ────────────────────────────────────────────────────────────────
CREATE INDEX IF NOT EXISTS idx_ip_allowlist_active_address
    ON admin_ip_allowlist (is_active, ip_address)
    WHERE is_active = TRUE;

-- ── System Setting: toggle for IP allowlist ────────────────────────────────
INSERT INTO system_settings (key, value, category, label, description, data_type, is_sensitive)
VALUES (
    'security.ip_allowlist_enabled',
    'false',
    'security',
    'IP Allowlist Enabled',
    'When enabled, only requests from allowed IP addresses can access admin endpoints.',
    'boolean',
    FALSE
)
ON CONFLICT (key) DO NOTHING;
