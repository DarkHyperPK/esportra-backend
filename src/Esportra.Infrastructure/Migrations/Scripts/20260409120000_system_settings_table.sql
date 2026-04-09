-- ============================================================================
-- Migration: System Settings (key-value config store)
-- ============================================================================

-- ── Table ──────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS system_settings (
    key          TEXT        PRIMARY KEY,
    value        TEXT        NOT NULL DEFAULT '',
    category     TEXT        NOT NULL DEFAULT 'general',
    label        TEXT        NOT NULL DEFAULT '',
    description  TEXT        DEFAULT '',
    data_type    TEXT        NOT NULL DEFAULT 'string',   -- string | boolean | number | email | url
    is_sensitive BOOLEAN     NOT NULL DEFAULT FALSE,
    updated_by   UUID        REFERENCES auth.users(id),
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ── Ensure columns exist (table may pre-exist from earlier migration) ────
ALTER TABLE system_settings ADD COLUMN IF NOT EXISTS category     TEXT NOT NULL DEFAULT 'general';
ALTER TABLE system_settings ADD COLUMN IF NOT EXISTS label        TEXT NOT NULL DEFAULT '';
ALTER TABLE system_settings ADD COLUMN IF NOT EXISTS description  TEXT DEFAULT '';
ALTER TABLE system_settings ADD COLUMN IF NOT EXISTS data_type    TEXT NOT NULL DEFAULT 'string';
ALTER TABLE system_settings ADD COLUMN IF NOT EXISTS is_sensitive BOOLEAN NOT NULL DEFAULT FALSE;

-- ── RLS ────────────────────────────────────────────────────────────────────
ALTER TABLE system_settings ENABLE ROW LEVEL SECURITY;

-- Non-sensitive settings readable by public (SponsorEndpoints branding query)
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'system_settings_public_read_non_sensitive' AND tablename = 'system_settings'
  ) THEN
    CREATE POLICY "system_settings_public_read_non_sensitive" ON system_settings
      FOR SELECT
      USING (is_sensitive = FALSE);
  END IF;
END; $$;

-- Authenticated users can read all non-sensitive settings
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'system_settings_authenticated_read' AND tablename = 'system_settings'
  ) THEN
    CREATE POLICY "system_settings_authenticated_read" ON system_settings
      FOR SELECT TO authenticated
      USING (is_sensitive = FALSE);
  END IF;
END; $$;

-- Admin-only write (insert/update/delete) via service_role; RPC or backend handles auth check
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'system_settings_service_role_all' AND tablename = 'system_settings'
  ) THEN
    CREATE POLICY "system_settings_service_role_all" ON system_settings
      FOR ALL TO service_role
      USING (TRUE)
      WITH CHECK (TRUE);
  END IF;
END; $$;

-- ── Grants ─────────────────────────────────────────────────────────────────
GRANT SELECT ON system_settings TO authenticated;
GRANT ALL    ON system_settings TO service_role;

-- ── Auto-update timestamp trigger ──────────────────────────────────────────
CREATE OR REPLACE FUNCTION update_system_settings_timestamp()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_trigger WHERE tgname = 'trg_system_settings_updated_at'
  ) THEN
    CREATE TRIGGER trg_system_settings_updated_at
      BEFORE UPDATE ON system_settings
      FOR EACH ROW
      EXECUTE FUNCTION update_system_settings_timestamp();
  END IF;
END; $$;

-- ── Seed default settings ──────────────────────────────────────────────────

-- Platform
INSERT INTO system_settings (key, value, category, label, description, data_type, is_sensitive) VALUES
  ('platform_name',       'Esportra', 'platform', 'Platform Name',        'The display name of the platform.',                            'string',  FALSE),
  ('platform_logo_url',   '',         'platform', 'Platform Logo URL',    'URL for the main logo shown in the header.',                   'url',     FALSE),
  ('platform_icon_url',   '',         'platform', 'Platform Icon URL',    'URL for the favicon / small icon.',                            'url',     FALSE),
  ('public_contact_email','',         'platform', 'Contact Email',        'Public-facing support email address.',                         'email',   FALSE),
  ('support_portal_url',  '',         'platform', 'Support Portal URL',   'Link to the external support / helpdesk portal.',              'url',     FALSE),
  ('maintenance_mode',    'false',    'platform', 'Maintenance Mode',     'When enabled, the platform shows a maintenance page.',         'boolean', FALSE),
  ('maintenance_message', 'We are currently performing maintenance. Please check back soon.',
                                      'platform', 'Maintenance Message',  'Message displayed during maintenance mode.',                   'string',  FALSE)
ON CONFLICT (key) DO NOTHING;

-- Registration
INSERT INTO system_settings (key, value, category, label, description, data_type, is_sensitive) VALUES
  ('registrations_enabled',       'true',  'registration', 'Registrations Enabled',       'Allow new user registrations.',                  'boolean', FALSE),
  ('email_verification_required', 'true',  'registration', 'Email Verification Required', 'Require email verification before login.',       'boolean', FALSE)
ON CONFLICT (key) DO NOTHING;

-- Tournaments
INSERT INTO system_settings (key, value, category, label, description, data_type, is_sensitive) VALUES
  ('max_tournaments_per_organizer', '20',   'tournaments', 'Max Tournaments per Organizer', 'Maximum number of active tournaments an organizer can have.',  'number',  FALSE),
  ('max_team_size',                 '10',   'tournaments', 'Max Team Size',                 'Maximum number of players per team.',                          'number',  FALSE),
  ('checkin_grace_minutes',         '5',    'tournaments', 'Check-in Grace Period (min)',   'Extra minutes allowed after check-in window closes.',          'number',  FALSE),
  ('require_payment_receipt',       'true', 'tournaments', 'Require Payment Receipt',      'Require payment receipt upload for paid tournaments.',         'boolean', FALSE)
ON CONFLICT (key) DO NOTHING;

-- Security
INSERT INTO system_settings (key, value, category, label, description, data_type, is_sensitive) VALUES
  ('max_login_attempts',      '5',   'security', 'Max Login Attempts',        'Lockout threshold for failed login attempts.',          'number', FALSE),
  ('session_timeout_minutes', '480', 'security', 'Session Timeout (minutes)', 'Idle session timeout before requiring re-login.',       'number', FALSE)
ON CONFLICT (key) DO NOTHING;

-- Storage
INSERT INTO system_settings (key, value, category, label, description, data_type, is_sensitive) VALUES
  ('max_upload_size_mb', '50', 'storage', 'Max Upload Size (MB)', 'Maximum file upload size in megabytes.', 'number', FALSE)
ON CONFLICT (key) DO NOTHING;

-- Notifications
INSERT INTO system_settings (key, value, category, label, description, data_type, is_sensitive) VALUES
  ('discord_webhook_url',        '',     'notifications', 'Discord Webhook URL',        'Webhook URL for Discord notifications.',    'url',     TRUE),
  ('email_notifications_enabled','true', 'notifications', 'Email Notifications Enabled','Global toggle for email notifications.',    'boolean', FALSE)
ON CONFLICT (key) DO NOTHING;
