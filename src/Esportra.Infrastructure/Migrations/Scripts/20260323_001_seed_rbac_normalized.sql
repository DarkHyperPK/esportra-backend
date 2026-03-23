-- ═══════════════════════════════════════════════════════════════
-- Normalized RBAC: seed roles, permissions, mappings, and sync trigger
-- ═══════════════════════════════════════════════════════════════

-- 1. Add super_admin to admin_roles catalog
INSERT INTO admin_roles (name, key, description)
VALUES ('super_admin', 'super_admin', 'Full system access — all permissions granted')
ON CONFLICT (name) DO NOTHING;

-- 2. Seed all 26 permissions
INSERT INTO admin_permissions (name, description, resource, action) VALUES
  ('users:view',          'View user profiles and lists',       'users',       'view'),
  ('users:edit',          'Edit user profiles',                 'users',       'edit'),
  ('users:ban',           'Ban/suspend users',                  'users',       'ban'),
  ('users:delete',        'Delete user accounts',               'users',       'delete'),
  ('tournaments:view',    'View tournament details',            'tournaments', 'view'),
  ('tournaments:edit',    'Edit tournament settings',           'tournaments', 'edit'),
  ('tournaments:delete',  'Delete tournaments',                 'tournaments', 'delete'),
  ('tournaments:create',  'Create tournaments',                 'tournaments', 'create'),
  ('disputes:view',       'View dispute cases',                 'disputes',    'view'),
  ('disputes:resolve',    'Resolve dispute cases',              'disputes',    'resolve'),
  ('disputes:delete',     'Delete dispute cases',               'disputes',    'delete'),
  ('venues:view',         'View venue listings',                'venues',      'view'),
  ('venues:approve',      'Approve venue applications',         'venues',      'approve'),
  ('venues:delete',       'Delete venue listings',              'venues',      'delete'),
  ('sponsors:view',       'View sponsor campaigns',             'sponsors',    'view'),
  ('sponsors:create',     'Create sponsor campaigns',           'sponsors',    'create'),
  ('sponsors:edit',       'Edit sponsor campaigns',             'sponsors',    'edit'),
  ('sponsors:delete',     'Delete sponsor campaigns',           'sponsors',    'delete'),
  ('analytics:view',      'View analytics dashboards',          'analytics',   'view'),
  ('analytics:export',    'Export analytics data',              'analytics',   'export'),
  ('system:settings',     'Manage system settings',             'system',      'settings'),
  ('system:audit',        'View audit logs',                    'system',      'audit'),
  ('system:billing',      'Manage billing and payments',        'system',      'billing'),
  ('content:moderate',    'Moderate user-generated content',    'content',     'moderate'),
  ('content:create',      'Create platform content',            'content',     'create'),
  ('content:delete',      'Delete platform content',            'content',     'delete')
ON CONFLICT (name) DO NOTHING;

-- 3. Unique constraint on role-permission mappings
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'admin_role_permissions_role_id_permission_id_key'
  ) THEN
    ALTER TABLE admin_role_permissions
      ADD CONSTRAINT admin_role_permissions_role_id_permission_id_key
      UNIQUE (role_id, permission_id);
  END IF;
END $$;

-- 4. Seed role → permission mappings
INSERT INTO admin_role_permissions (role_id, permission_id)
SELECT ar.id, ap.id FROM admin_roles ar, admin_permissions ap
WHERE ar.key = 'ops_admin' AND ap.name IN (
  'users:view','users:edit','users:ban',
  'tournaments:view','tournaments:edit','tournaments:delete',
  'disputes:view','disputes:resolve',
  'venues:view','venues:approve',
  'analytics:view','system:audit')
ON CONFLICT DO NOTHING;

INSERT INTO admin_role_permissions (role_id, permission_id)
SELECT ar.id, ap.id FROM admin_roles ar, admin_permissions ap
WHERE ar.key = 'moderator' AND ap.name IN (
  'users:view','users:ban',
  'disputes:view','disputes:resolve',
  'content:moderate','content:delete',
  'tournaments:view')
ON CONFLICT DO NOTHING;

INSERT INTO admin_role_permissions (role_id, permission_id)
SELECT ar.id, ap.id FROM admin_roles ar, admin_permissions ap
WHERE ar.key = 'finance_admin' AND ap.name IN (
  'analytics:view','analytics:export',
  'system:billing',
  'sponsors:view','sponsors:create','sponsors:edit')
ON CONFLICT DO NOTHING;

INSERT INTO admin_role_permissions (role_id, permission_id)
SELECT ar.id, ap.id FROM admin_roles ar, admin_permissions ap
WHERE ar.key = 'support_admin' AND ap.name IN (
  'users:view','disputes:view',
  'tournaments:view','venues:view')
ON CONFLICT DO NOTHING;

INSERT INTO admin_role_permissions (role_id, permission_id)
SELECT ar.id, ap.id FROM admin_roles ar, admin_permissions ap
WHERE ar.key = 'super_admin'
ON CONFLICT DO NOTHING;

-- 5. Trigger: sync profiles.is_admin + profiles.admin_roles from admin_user_roles
CREATE OR REPLACE FUNCTION fn_sync_admin_profile()
RETURNS TRIGGER LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE
  target_user_id UUID;
  role_array     TEXT[];
BEGIN
  IF TG_OP = 'DELETE' THEN
    target_user_id := OLD.user_id;
  ELSE
    target_user_id := NEW.user_id;
  END IF;

  SELECT ARRAY_AGG(ar.key)
    INTO role_array
    FROM admin_user_roles aur
    JOIN admin_roles ar ON ar.id = aur.role_id
   WHERE aur.user_id = target_user_id;

  UPDATE profiles
     SET admin_roles = COALESCE(role_array, '{}'),
         is_admin    = (role_array IS NOT NULL AND array_length(role_array, 1) > 0)
   WHERE id = target_user_id;

  RETURN COALESCE(NEW, OLD);
END;
$$;

DROP TRIGGER IF EXISTS trg_sync_admin_profile ON admin_user_roles;
CREATE TRIGGER trg_sync_admin_profile
  AFTER INSERT OR UPDATE OR DELETE ON admin_user_roles
  FOR EACH ROW EXECUTE FUNCTION fn_sync_admin_profile();

-- 6. Migrate existing profiles.admin_roles data into admin_user_roles
INSERT INTO admin_user_roles (user_id, role_id, assigned_by)
SELECT p.id, ar.id, NULL
FROM profiles p,
     LATERAL UNNEST(p.admin_roles) AS role_key
JOIN admin_roles ar ON ar.key = role_key
WHERE p.admin_roles IS NOT NULL
  AND array_length(p.admin_roles, 1) > 0
ON CONFLICT (user_id, role_id) DO NOTHING;
