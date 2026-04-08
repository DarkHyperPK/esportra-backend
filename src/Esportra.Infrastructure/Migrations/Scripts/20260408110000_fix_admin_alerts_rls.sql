-- Fix: restrict admin_alerts to admin users only (not all authenticated users)
-- The original migration allowed all authenticated users to SELECT, leaking admin data.

-- Drop the overly-permissive policy
DROP POLICY IF EXISTS admin_alerts_authenticated_read ON admin_alerts;

-- Revoke the blanket authenticated grant
REVOKE SELECT ON admin_alerts FROM authenticated;

-- Create a proper admin-only read policy
DO $$ BEGIN
IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'admin_alerts' AND policyname = 'admin_alerts_admin_read') THEN
    CREATE POLICY admin_alerts_admin_read ON admin_alerts
        FOR SELECT TO authenticated
        USING (EXISTS (SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid()));
END IF;
END $$;

-- Grant SELECT back only through RLS (policy gates it)
GRANT SELECT ON admin_alerts TO authenticated;
