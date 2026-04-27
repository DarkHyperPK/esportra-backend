CREATE TABLE IF NOT EXISTS admin_dashboard_preferences (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    layout JSONB NOT NULL DEFAULT '[]',
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_dashboard_prefs_user UNIQUE (user_id)
);

ALTER TABLE admin_dashboard_preferences ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'dashboard_prefs_read_own' AND tablename = 'admin_dashboard_preferences'
  ) THEN
    CREATE POLICY "dashboard_prefs_read_own" ON admin_dashboard_preferences
      FOR SELECT USING (auth.uid() = user_id);
  END IF;
END; $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'dashboard_prefs_insert_own' AND tablename = 'admin_dashboard_preferences'
  ) THEN
    CREATE POLICY "dashboard_prefs_insert_own" ON admin_dashboard_preferences
      FOR INSERT WITH CHECK (auth.uid() = user_id);
  END IF;
END; $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'dashboard_prefs_update_own' AND tablename = 'admin_dashboard_preferences'
  ) THEN
    CREATE POLICY "dashboard_prefs_update_own" ON admin_dashboard_preferences
      FOR UPDATE USING (auth.uid() = user_id);
  END IF;
END; $$;

GRANT ALL ON admin_dashboard_preferences TO service_role;
GRANT SELECT, INSERT, UPDATE ON admin_dashboard_preferences TO authenticated;
