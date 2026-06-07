-- Seed games catalog permissions for admin-managed catalog.

INSERT INTO admin_permissions (name, description, resource, action) VALUES
  ('games:view',   'View game catalog',           'games', 'view'),
  ('games:manage', 'Manage and publish game catalog', 'games', 'manage')
ON CONFLICT (name) DO NOTHING;

INSERT INTO admin_role_permissions (role_id, permission_id)
SELECT ar.id, ap.id FROM admin_roles ar, admin_permissions ap
WHERE ar.key = 'ops_admin' AND ap.name IN ('games:view', 'games:manage')
ON CONFLICT DO NOTHING;

INSERT INTO admin_role_permissions (role_id, permission_id)
SELECT ar.id, ap.id FROM admin_roles ar, admin_permissions ap
WHERE ar.key = 'super_admin' AND ap.name IN ('games:view', 'games:manage')
ON CONFLICT DO NOTHING;
