-- ============================================================================
-- Migration: Seed developer_keys:manage Permission
-- Purpose:   Registers the developer_keys:manage permission in the admin RBAC
--            catalog and assigns it to ops_admin and super_admin roles.
--            All INSERTs use ON CONFLICT DO NOTHING for full idempotency.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. Seed permission into admin_permissions
-- ---------------------------------------------------------------------------
INSERT INTO public.admin_permissions (name, description, resource, action)
VALUES (
    'developer_keys:manage',
    'Approve organizations for API access, view and revoke developer API keys',
    'developer_keys',
    'manage'
)
ON CONFLICT (name) DO NOTHING;

-- ---------------------------------------------------------------------------
-- 2. Assign to ops_admin and super_admin roles
-- ---------------------------------------------------------------------------
INSERT INTO public.admin_role_permissions (role_id, permission_id)
SELECT ar.id, ap.id
FROM   public.admin_roles ar, public.admin_permissions ap
WHERE  ar.key = 'ops_admin'
  AND  ap.name = 'developer_keys:manage'
ON CONFLICT DO NOTHING;

INSERT INTO public.admin_role_permissions (role_id, permission_id)
SELECT ar.id, ap.id
FROM   public.admin_roles ar, public.admin_permissions ap
WHERE  ar.key = 'super_admin'
  AND  ap.name = 'developer_keys:manage'
ON CONFLICT DO NOTHING;
