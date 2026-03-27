-- Add unique constraints on (user_id, role) for user_roles and verified_roles
-- Required for ON CONFLICT upserts in license reinstate/assign operations

CREATE UNIQUE INDEX IF NOT EXISTS uq_user_roles_user_role
  ON public.user_roles (user_id, role);

CREATE UNIQUE INDEX IF NOT EXISTS uq_verified_roles_user_role
  ON public.verified_roles (user_id, role);
