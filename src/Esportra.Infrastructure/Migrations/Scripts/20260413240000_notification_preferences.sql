-- ============================================================
-- Phase 12: Notification Preferences per venue/user
-- ============================================================

-- ── Table ────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS notification_preferences (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    venue_id        UUID NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
    user_id         UUID NOT NULL,
    station_offline BOOLEAN NOT NULL DEFAULT true,
    low_balance     BOOLEAN NOT NULL DEFAULT true,
    new_booking     BOOLEAN NOT NULL DEFAULT true,
    order_ready     BOOLEAN NOT NULL DEFAULT true,
    session_ending  BOOLEAN NOT NULL DEFAULT true,
    staff_clock     BOOLEAN NOT NULL DEFAULT false,
    tamper_alert    BOOLEAN NOT NULL DEFAULT true,
    walk_in_queue   BOOLEAN NOT NULL DEFAULT true,
    daily_summary   BOOLEAN NOT NULL DEFAULT false,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE(venue_id, user_id)
);

-- ── RLS ──────────────────────────────────────────────────────
ALTER TABLE notification_preferences ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE policyname = 'np_select_own' AND tablename = 'notification_preferences'
    ) THEN
        CREATE POLICY "np_select_own" ON notification_preferences
            FOR SELECT USING (auth.uid() = user_id);
    END IF;
END; $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE policyname = 'np_insert_own' AND tablename = 'notification_preferences'
    ) THEN
        CREATE POLICY "np_insert_own" ON notification_preferences
            FOR INSERT WITH CHECK (auth.uid() = user_id);
    END IF;
END; $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE policyname = 'np_update_own' AND tablename = 'notification_preferences'
    ) THEN
        CREATE POLICY "np_update_own" ON notification_preferences
            FOR UPDATE USING (auth.uid() = user_id);
    END IF;
END; $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE policyname = 'np_delete_own' AND tablename = 'notification_preferences'
    ) THEN
        CREATE POLICY "np_delete_own" ON notification_preferences
            FOR DELETE USING (auth.uid() = user_id);
    END IF;
END; $$;

-- ── GRANTs ───────────────────────────────────────────────────
GRANT SELECT, INSERT, UPDATE, DELETE ON notification_preferences TO authenticated;
GRANT SELECT, INSERT, UPDATE, DELETE ON notification_preferences TO service_role;

-- ── Index on user_id for faster lookups ──────────────────────
CREATE INDEX IF NOT EXISTS idx_notification_preferences_user_id
    ON notification_preferences (user_id);
