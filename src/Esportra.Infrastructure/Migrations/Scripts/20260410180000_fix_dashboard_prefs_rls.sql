-- Drop existing permissive INSERT/UPDATE policies that allowed any authenticated user
DROP POLICY IF EXISTS dashboard_prefs_insert_own ON admin_dashboard_preferences;
DROP POLICY IF EXISTS dashboard_prefs_update_own ON admin_dashboard_preferences;

-- Recreate INSERT policy requiring admin role
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'dashboard_prefs_insert_admin' AND tablename = 'admin_dashboard_preferences') THEN
    CREATE POLICY dashboard_prefs_insert_admin ON admin_dashboard_preferences
      FOR INSERT WITH CHECK (
        auth.uid() = user_id
        AND EXISTS (SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid())
      );
  END IF;
END; $$;

-- Recreate UPDATE policy requiring admin role
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'dashboard_prefs_update_admin' AND tablename = 'admin_dashboard_preferences') THEN
    CREATE POLICY dashboard_prefs_update_admin ON admin_dashboard_preferences
      FOR UPDATE USING (
        auth.uid() = user_id
        AND EXISTS (SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid())
      )
      WITH CHECK (auth.uid() = user_id);
  END IF;
END; $$;
