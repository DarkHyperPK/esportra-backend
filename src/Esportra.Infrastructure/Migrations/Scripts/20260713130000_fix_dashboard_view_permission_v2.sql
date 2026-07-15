-- Fix: dashboard:view permission row missing from admin_permissions on environments
-- where the v2 catalog migration ran before this permission was added.
-- The previous fix (20260713120000) only inserted the role mapping but silently
-- produced zero rows because the permission itself didn't exist.

-- 1. Ensure the permission row exists
INSERT INTO admin_permissions (name, description, resource, action, label, category, risk_level, sort_order, is_system, updated_at)
VALUES ('dashboard:view', 'View permission for Dashboard', 'dashboard', 'view', 'View', 'Dashboard', 'normal', 2902, TRUE, now())
ON CONFLICT (name) DO NOTHING;

-- 2. Map it to all admin roles that should access the dashboard
INSERT INTO admin_role_permissions (role_id, permission_id)
SELECT ar.id, ap.id
FROM admin_roles ar
CROSS JOIN admin_permissions ap
WHERE ap.name = 'dashboard:view'
  AND ar.key IN ('ops_admin', 'moderator', 'finance_admin', 'support_admin')
ON CONFLICT DO NOTHING;
