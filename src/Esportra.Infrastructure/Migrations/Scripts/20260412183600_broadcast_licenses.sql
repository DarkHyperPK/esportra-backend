-- ============================================================================
-- Migration: 20260412183600_broadcast_licenses.sql
-- Purpose:   Broadcast license management for the Esportra Broadcaster desktop app.
--            Tracks paid subscriptions/one-time purchases with server-side validation.
-- ============================================================================

CREATE TABLE IF NOT EXISTS broadcast_licenses (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id           UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    license_key       TEXT UNIQUE NOT NULL,
    license_type      TEXT NOT NULL CHECK (license_type IN ('subscription', 'one_time')),
    status            TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'expired', 'revoked', 'suspended')),
    plan              TEXT NOT NULL DEFAULT 'standard' CHECK (plan IN ('standard', 'pro', 'enterprise')),
    activated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at        TIMESTAMPTZ,
    last_validated_at TIMESTAMPTZ DEFAULT now(),
    device_fingerprints JSONB DEFAULT '[]',
    max_devices       INT NOT NULL DEFAULT 2,
    metadata          JSONB DEFAULT '{}',
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);

ALTER TABLE broadcast_licenses ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'broadcast_licenses_service_all' AND tablename = 'broadcast_licenses') THEN
    CREATE POLICY broadcast_licenses_service_all ON broadcast_licenses
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'broadcast_licenses_own' AND tablename = 'broadcast_licenses') THEN
    CREATE POLICY broadcast_licenses_own ON broadcast_licenses
      FOR ALL USING (auth.uid() = user_id);
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_broadcast_licenses_user_id ON broadcast_licenses(user_id);
CREATE INDEX IF NOT EXISTS idx_broadcast_licenses_license_key ON broadcast_licenses(license_key);
CREATE INDEX IF NOT EXISTS idx_broadcast_licenses_status ON broadcast_licenses(status);

GRANT ALL ON broadcast_licenses TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON broadcast_licenses TO authenticated;
