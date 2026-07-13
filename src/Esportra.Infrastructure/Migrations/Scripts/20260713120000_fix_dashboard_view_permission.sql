-- Fix: ensure dashboard:view permission is mapped to all admin roles that need dashboard access.
-- The permission exists in admin_permissions (seeded by v2 catalog) but the role-permission
-- mapping may be missing on environments where the v2 migration ran before dashboard:view was added,
-- or where support_admin was never granted it.

INSERT INTO admin_role_permissions (role_id, permission_id)
SELECT ar.id, ap.id
FROM admin_roles ar
CROSS JOIN admin_permissions ap
WHERE ap.name = 'dashboard:view'
  AND ar.key IN ('ops_admin', 'moderator', 'finance_admin', 'support_admin')
ON CONFLICT DO NOTHING;
