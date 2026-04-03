-- Phase 2D: Billing config table + extend venue_sessions

CREATE TABLE IF NOT EXISTS venue_billing_config (
  id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  venue_id              UUID NOT NULL REFERENCES venues(id) ON DELETE CASCADE UNIQUE,
  zones                 JSONB NOT NULL DEFAULT '[]',
  packages              JSONB NOT NULL DEFAULT '[]',
  vouchers              JSONB NOT NULL DEFAULT '[]',
  grace_period_minutes  INT NOT NULL DEFAULT 5,
  currency              TEXT NOT NULL DEFAULT 'GBP',
  updated_at            TIMESTAMPTZ DEFAULT NOW()
);

ALTER TABLE venue_billing_config ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
IF NOT EXISTS (
  SELECT 1 FROM pg_policies WHERE tablename = 'venue_billing_config' AND policyname = 'billing_config_service_all'
) THEN
  CREATE POLICY billing_config_service_all ON venue_billing_config
    FOR ALL
    USING (auth.role() = 'service_role')
    WITH CHECK (auth.role() = 'service_role');
END IF;
END $$;

DO $$ BEGIN
IF NOT EXISTS (
  SELECT 1 FROM pg_policies WHERE tablename = 'venue_billing_config' AND policyname = 'billing_config_owner_select'
) THEN
  CREATE POLICY billing_config_owner_select ON venue_billing_config
    FOR SELECT
    USING (
      EXISTS (
        SELECT 1 FROM venues v WHERE v.id = venue_billing_config.venue_id AND v.owner_id = auth.uid()
      )
    );
END IF;
END $$;

-- Extend venue_sessions with zone, package, and staff tracking
ALTER TABLE venue_sessions
  ADD COLUMN IF NOT EXISTS zone       TEXT,
  ADD COLUMN IF NOT EXISTS package_id TEXT,
  ADD COLUMN IF NOT EXISTS staff_id   UUID;
