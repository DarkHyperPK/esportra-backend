-- ============================================================================
-- Migration: Tighten admin_ip_allowlist RLS policies to super_admin only
-- The original policies allowed any admin role; IP allowlist management
-- requires super_admin to match the API endpoint authorization check.
-- ============================================================================

-- Drop the over-permissive policies created in 20260410110000_ip_allowlist.sql
DROP POLICY IF EXISTS "admin_ip_allowlist_select" ON admin_ip_allowlist;
DROP POLICY IF EXISTS "admin_ip_allowlist_insert" ON admin_ip_allowlist;
DROP POLICY IF EXISTS "admin_ip_allowlist_update" ON admin_ip_allowlist;
DROP POLICY IF EXISTS "admin_ip_allowlist_delete" ON admin_ip_allowlist;

-- SELECT: super_admin only
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'ip_allowlist_super_admin_select' AND tablename = 'admin_ip_allowlist'
  ) THEN
    CREATE POLICY "ip_allowlist_super_admin_select" ON admin_ip_allowlist
      FOR SELECT TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM admin_user_roles aur
          JOIN admin_roles ar ON ar.id = aur.role_id
          WHERE aur.user_id = auth.uid() AND ar.key = 'super_admin'
        )
      );
  END IF;
END; $$;

-- INSERT: super_admin only
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'ip_allowlist_super_admin_insert' AND tablename = 'admin_ip_allowlist'
  ) THEN
    CREATE POLICY "ip_allowlist_super_admin_insert" ON admin_ip_allowlist
      FOR INSERT TO authenticated
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM admin_user_roles aur
          JOIN admin_roles ar ON ar.id = aur.role_id
          WHERE aur.user_id = auth.uid() AND ar.key = 'super_admin'
        )
      );
  END IF;
END; $$;

-- UPDATE: super_admin only
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'ip_allowlist_super_admin_update' AND tablename = 'admin_ip_allowlist'
  ) THEN
    CREATE POLICY "ip_allowlist_super_admin_update" ON admin_ip_allowlist
      FOR UPDATE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM admin_user_roles aur
          JOIN admin_roles ar ON ar.id = aur.role_id
          WHERE aur.user_id = auth.uid() AND ar.key = 'super_admin'
        )
      )
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM admin_user_roles aur
          JOIN admin_roles ar ON ar.id = aur.role_id
          WHERE aur.user_id = auth.uid() AND ar.key = 'super_admin'
        )
      );
  END IF;
END; $$;

-- DELETE: super_admin only
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'ip_allowlist_super_admin_delete' AND tablename = 'admin_ip_allowlist'
  ) THEN
    CREATE POLICY "ip_allowlist_super_admin_delete" ON admin_ip_allowlist
      FOR DELETE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM admin_user_roles aur
          JOIN admin_roles ar ON ar.id = aur.role_id
          WHERE aur.user_id = auth.uid() AND ar.key = 'super_admin'
        )
      );
  END IF;
END; $$;
