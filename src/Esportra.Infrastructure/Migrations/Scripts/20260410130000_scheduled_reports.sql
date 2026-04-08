-- ============================================================================
-- Migration: Scheduled Reports — report_schedules + report_run_log tables
-- Admins can create recurring report schedules (daily/weekly/monthly) that
-- get executed automatically or triggered manually.
-- ============================================================================

-- ── report_schedules ─────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS report_schedules (
    id             UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    name           TEXT        NOT NULL,
    report_type    TEXT        NOT NULL,   -- 'users','tournaments','revenue','activity','moderation'
    frequency      TEXT        NOT NULL,   -- 'daily','weekly','monthly'
    day_of_week    INT,                    -- 0-6 for weekly (0=Sunday)
    day_of_month   INT,                    -- 1-31 for monthly
    time_of_day    TIME        NOT NULL DEFAULT '08:00',
    recipients     TEXT[]      NOT NULL DEFAULT '{}',
    format         TEXT        NOT NULL DEFAULT 'csv',  -- 'csv','json'
    filters        JSONB       NOT NULL DEFAULT '{}',
    is_active      BOOLEAN     NOT NULL DEFAULT TRUE,
    last_run_at    TIMESTAMPTZ,
    next_run_at    TIMESTAMPTZ,
    created_by     UUID        REFERENCES auth.users(id),
    created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ── report_run_log ───────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS report_run_log (
    id               UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    schedule_id      UUID        NOT NULL REFERENCES report_schedules(id) ON DELETE CASCADE,
    status           TEXT        NOT NULL DEFAULT 'running',  -- 'running','completed','failed'
    started_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at     TIMESTAMPTZ,
    row_count        INT,
    file_size_bytes  BIGINT,
    error_message    TEXT,
    download_url     TEXT,
    triggered_by     TEXT        NOT NULL DEFAULT 'schedule'  -- 'schedule','manual'
);

-- ── Indexes ──────────────────────────────────────────────────────────────────
CREATE INDEX IF NOT EXISTS idx_report_schedules_active_next
    ON report_schedules (is_active, next_run_at)
    WHERE is_active = TRUE;

CREATE INDEX IF NOT EXISTS idx_report_run_log_schedule_started
    ON report_run_log (schedule_id, started_at DESC);

-- ── updated_at auto-update trigger ───────────────────────────────────────────
CREATE OR REPLACE FUNCTION update_report_schedules_updated_at()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_report_schedules_updated_at ON report_schedules;
CREATE TRIGGER trg_report_schedules_updated_at
    BEFORE UPDATE ON report_schedules
    FOR EACH ROW EXECUTE FUNCTION update_report_schedules_updated_at();

-- ── RLS ──────────────────────────────────────────────────────────────────────
ALTER TABLE report_schedules  ENABLE ROW LEVEL SECURITY;
ALTER TABLE report_run_log    ENABLE ROW LEVEL SECURITY;

-- Helper: any admin role check
-- report_schedules policies
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'report_schedules_admin_select' AND tablename = 'report_schedules'
  ) THEN
    CREATE POLICY "report_schedules_admin_select" ON report_schedules
      FOR SELECT TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid()
        )
      );
  END IF;
END; $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'report_schedules_admin_insert' AND tablename = 'report_schedules'
  ) THEN
    CREATE POLICY "report_schedules_admin_insert" ON report_schedules
      FOR INSERT TO authenticated
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid()
        )
      );
  END IF;
END; $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'report_schedules_admin_update' AND tablename = 'report_schedules'
  ) THEN
    CREATE POLICY "report_schedules_admin_update" ON report_schedules
      FOR UPDATE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid()
        )
      )
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid()
        )
      );
  END IF;
END; $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'report_schedules_admin_delete' AND tablename = 'report_schedules'
  ) THEN
    CREATE POLICY "report_schedules_admin_delete" ON report_schedules
      FOR DELETE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid()
        )
      );
  END IF;
END; $$;

-- report_run_log policies
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'report_run_log_admin_select' AND tablename = 'report_run_log'
  ) THEN
    CREATE POLICY "report_run_log_admin_select" ON report_run_log
      FOR SELECT TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid()
        )
      );
  END IF;
END; $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'report_run_log_admin_insert' AND tablename = 'report_run_log'
  ) THEN
    CREATE POLICY "report_run_log_admin_insert" ON report_run_log
      FOR INSERT TO authenticated
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid()
        )
      );
  END IF;
END; $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'report_run_log_admin_update' AND tablename = 'report_run_log'
  ) THEN
    CREATE POLICY "report_run_log_admin_update" ON report_run_log
      FOR UPDATE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid()
        )
      )
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid()
        )
      );
  END IF;
END; $$;

-- ── GRANTs ───────────────────────────────────────────────────────────────────
GRANT ALL   ON report_schedules TO service_role;
GRANT SELECT ON report_schedules TO authenticated;

GRANT ALL   ON report_run_log TO service_role;
GRANT SELECT ON report_run_log TO authenticated;
