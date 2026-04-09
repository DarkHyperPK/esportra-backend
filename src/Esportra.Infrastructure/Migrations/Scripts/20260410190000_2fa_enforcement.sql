-- ============================================================
-- Phase 16: 2FA Enforcement Settings
-- Inserts two security settings into system_settings.
-- Safe to re-run (ON CONFLICT DO NOTHING).
-- ============================================================

INSERT INTO system_settings (key, value, description, category, updated_at)
VALUES
    (
        'security.2fa_required_roles',
        '["super_admin"]',
        'Roles that must have 2FA enabled (JSON array of role keys)',
        'security',
        NOW()
    ),
    (
        'security.2fa_enforcement_enabled',
        'false',
        'Whether 2FA enforcement is active platform-wide',
        'security',
        NOW()
    )
ON CONFLICT (key) DO NOTHING;
