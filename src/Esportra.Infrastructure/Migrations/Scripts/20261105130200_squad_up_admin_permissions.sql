-- Admin Centre permissions for Squad Up: view stats and sessions; manage (end a session).
INSERT INTO admin_permissions (name, description, resource, action) VALUES
  ('squad_up:view',   'View Squad Up stats, live sessions and matches', 'squad_up', 'view'),
  ('squad_up:manage', 'End Squad Up sessions',                          'squad_up', 'manage')
ON CONFLICT (name) DO NOTHING;

INSERT INTO admin_role_permissions (role_id, permission_id)
SELECT ar.id, ap.id FROM admin_roles ar, admin_permissions ap
WHERE ar.key IN ('ops_admin', 'super_admin') AND ap.name IN ('squad_up:view', 'squad_up:manage')
ON CONFLICT DO NOTHING;

INSERT INTO admin_role_permissions (role_id, permission_id)
SELECT ar.id, ap.id FROM admin_roles ar, admin_permissions ap
WHERE ar.key = 'moderator' AND ap.name IN ('squad_up:view', 'squad_up:manage')
ON CONFLICT DO NOTHING;
