-- ============================================================================
-- Migration: GDPR Compliance — gdpr_requests + consent_records tables
-- Provides data export (right to access) and deletion (right to erasure)
-- workflows, plus a full consent audit trail.
-- ============================================================================

-- ── gdpr_requests ─────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS gdpr_requests (
    id             UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id        UUID        NOT NULL REFERENCES auth.users(id),
    request_type   TEXT        NOT NULL CHECK (request_type IN ('export', 'deletion')),
    status         TEXT        NOT NULL DEFAULT 'pending'
                               CHECK (status IN ('pending', 'processing', 'completed', 'failed', 'cancelled')),
    requested_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    processed_at   TIMESTAMPTZ,
    processed_by   UUID        REFERENCES auth.users(id),
    notes          TEXT        NOT NULL DEFAULT '',
    download_url   TEXT,
    expires_at     TIMESTAMPTZ
);

-- ── consent_records ──────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS consent_records (
    id            UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id       UUID        NOT NULL REFERENCES auth.users(id),
    consent_type  TEXT        NOT NULL,
    granted       BOOLEAN     NOT NULL,
    ip_address    TEXT,
    user_agent    TEXT,
    recorded_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    version       TEXT        NOT NULL DEFAULT '1.0'
);

-- ── Indexes ──────────────────────────────────────────────────────────────────
CREATE INDEX IF NOT EXISTS idx_gdpr_requests_user_id
    ON gdpr_requests (user_id);

CREATE INDEX IF NOT EXISTS idx_gdpr_requests_status_type
    ON gdpr_requests (status, request_type);

CREATE INDEX IF NOT EXISTS idx_gdpr_requests_requested_at
    ON gdpr_requests (requested_at DESC);

CREATE INDEX IF NOT EXISTS idx_consent_records_user_id
    ON consent_records (user_id);

CREATE INDEX IF NOT EXISTS idx_consent_records_type_granted
    ON consent_records (consent_type, granted);

CREATE INDEX IF NOT EXISTS idx_consent_records_recorded_at
    ON consent_records (recorded_at DESC);

-- ── RLS ──────────────────────────────────────────────────────────────────────
ALTER TABLE gdpr_requests    ENABLE ROW LEVEL SECURITY;
ALTER TABLE consent_records  ENABLE ROW LEVEL SECURITY;

-- gdpr_requests: users can insert/select their own rows ----------------------
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'gdpr_requests_user_select' AND tablename = 'gdpr_requests'
  ) THEN
    CREATE POLICY "gdpr_requests_user_select" ON gdpr_requests
      FOR SELECT TO authenticated
      USING (auth.uid() = user_id);
  END IF;
END; $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'gdpr_requests_user_insert' AND tablename = 'gdpr_requests'
  ) THEN
    CREATE POLICY "gdpr_requests_user_insert" ON gdpr_requests
      FOR INSERT TO authenticated
      WITH CHECK (auth.uid() = user_id);
  END IF;
END; $$;

-- gdpr_requests: admins can select/update all rows ---------------------------
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'gdpr_requests_admin_select' AND tablename = 'gdpr_requests'
  ) THEN
    CREATE POLICY "gdpr_requests_admin_select" ON gdpr_requests
      FOR SELECT TO authenticated
      USING (
        EXISTS (SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid())
      );
  END IF;
END; $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'gdpr_requests_admin_update' AND tablename = 'gdpr_requests'
  ) THEN
    CREATE POLICY "gdpr_requests_admin_update" ON gdpr_requests
      FOR UPDATE TO authenticated
      USING (
        EXISTS (SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid())
      )
      WITH CHECK (
        EXISTS (SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid())
      );
  END IF;
END; $$;

-- consent_records: users can insert/select their own rows --------------------
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'consent_records_user_select' AND tablename = 'consent_records'
  ) THEN
    CREATE POLICY "consent_records_user_select" ON consent_records
      FOR SELECT TO authenticated
      USING (auth.uid() = user_id);
  END IF;
END; $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'consent_records_user_insert' AND tablename = 'consent_records'
  ) THEN
    CREATE POLICY "consent_records_user_insert" ON consent_records
      FOR INSERT TO authenticated
      WITH CHECK (auth.uid() = user_id);
  END IF;
END; $$;

-- consent_records: admins can select all rows --------------------------------
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'consent_records_admin_select' AND tablename = 'consent_records'
  ) THEN
    CREATE POLICY "consent_records_admin_select" ON consent_records
      FOR SELECT TO authenticated
      USING (
        EXISTS (SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid())
      );
  END IF;
END; $$;

-- ── GRANTs ───────────────────────────────────────────────────────────────────
GRANT ALL    ON gdpr_requests   TO service_role;
GRANT SELECT, INSERT ON gdpr_requests   TO authenticated;

GRANT ALL    ON consent_records TO service_role;
GRANT SELECT, INSERT ON consent_records TO authenticated;
