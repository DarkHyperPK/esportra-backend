-- Enterprise operations infrastructure: ABAC scopes, immutable audit, global config,
-- and short-lived impersonation session tracking.

CREATE TABLE IF NOT EXISTS public.admin_game_assignments (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  admin_id UUID NOT NULL REFERENCES public.profiles(id) ON DELETE CASCADE,
  game_id TEXT NOT NULL,
  scope TEXT NOT NULL DEFAULT 'tournament_ops',
  assigned_by UUID REFERENCES public.profiles(id),
  assigned_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  expires_at TIMESTAMPTZ,
  UNIQUE (admin_id, game_id, scope)
);

CREATE INDEX IF NOT EXISTS idx_admin_game_assignments_admin_scope
  ON public.admin_game_assignments(admin_id, scope, game_id, expires_at);

CREATE TABLE IF NOT EXISTS public.operations_audit_log (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  admin_id UUID NOT NULL,
  impersonated_user_id UUID,
  action_string TEXT NOT NULL,
  target_id TEXT,
  resource_type TEXT NOT NULL,
  ip_address INET,
  user_agent TEXT,
  request_method TEXT,
  request_path TEXT,
  severity TEXT NOT NULL DEFAULT 'low',
  data_diff JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

ALTER TABLE public.operations_audit_log ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE schemaname = 'public'
      AND tablename = 'operations_audit_log'
      AND policyname = 'operations_audit_log_service_role_all'
  ) THEN
    CREATE POLICY operations_audit_log_service_role_all
      ON public.operations_audit_log
      FOR ALL TO service_role
      USING (true)
      WITH CHECK (true);
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_operations_audit_target
  ON public.operations_audit_log(resource_type, target_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_operations_audit_admin
  ON public.operations_audit_log(admin_id, created_at DESC);

CREATE TABLE IF NOT EXISTS public.system_config (
  key TEXT PRIMARY KEY,
  value JSONB NOT NULL DEFAULT 'null'::jsonb,
  category TEXT NOT NULL DEFAULT 'general',
  label TEXT NOT NULL,
  description TEXT NOT NULL DEFAULT '',
  data_type TEXT NOT NULL DEFAULT 'boolean',
  is_kill_switch BOOLEAN NOT NULL DEFAULT false,
  is_sensitive BOOLEAN NOT NULL DEFAULT false,
  updated_by UUID REFERENCES public.profiles(id),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  CHECK (data_type IN ('boolean', 'number', 'string', 'json'))
);

CREATE OR REPLACE FUNCTION public.touch_system_config_updated_at()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
  NEW.updated_at = now();
  RETURN NEW;
END;
$$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_trigger WHERE tgname = 'trg_system_config_updated_at'
  ) THEN
    CREATE TRIGGER trg_system_config_updated_at
      BEFORE UPDATE ON public.system_config
      FOR EACH ROW
      EXECUTE FUNCTION public.touch_system_config_updated_at();
  END IF;
END $$;

ALTER TABLE public.system_config ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE schemaname = 'public'
      AND tablename = 'system_config'
      AND policyname = 'system_config_service_role_all'
  ) THEN
    CREATE POLICY system_config_service_role_all
      ON public.system_config
      FOR ALL TO service_role
      USING (true)
      WITH CHECK (true);
  END IF;
END $$;

INSERT INTO public.system_config (key, value, category, label, description, data_type, is_kill_switch)
VALUES
  ('platform.maintenance_mode', 'false'::jsonb, 'kill_switches', 'Maintenance Mode', 'Globally places the platform into maintenance posture.', 'boolean', true),
  ('tournaments.registration_lock', 'false'::jsonb, 'kill_switches', 'Tournament Registration Lock', 'Blocks new tournament registrations without disabling browsing.', 'boolean', true),
  ('payments.disable_charges', 'false'::jsonb, 'kill_switches', 'Disable Payment Charges', 'Stops all new payment charge attempts while preserving read-only billing views.', 'boolean', true),
  ('impersonation.enabled', 'true'::jsonb, 'security', 'Ghost Mode Enabled', 'Allows SuperAdmins to create short-lived scoped impersonation sessions.', 'boolean', false)
ON CONFLICT (key) DO NOTHING;

CREATE TABLE IF NOT EXISTS public.admin_impersonation_sessions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  jti TEXT NOT NULL UNIQUE,
  admin_id UUID NOT NULL REFERENCES public.profiles(id) ON DELETE CASCADE,
  target_user_id UUID NOT NULL REFERENCES public.profiles(id) ON DELETE CASCADE,
  reason TEXT NOT NULL,
  scopes TEXT[] NOT NULL DEFAULT ARRAY['support:read']::TEXT[],
  expires_at TIMESTAMPTZ NOT NULL,
  revoked_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  last_seen_at TIMESTAMPTZ,
  CHECK (expires_at <= created_at + INTERVAL '15 minutes')
);

CREATE INDEX IF NOT EXISTS idx_admin_impersonation_sessions_active
  ON public.admin_impersonation_sessions(jti, expires_at)
  WHERE revoked_at IS NULL;

GRANT SELECT ON public.system_config TO authenticated;
GRANT ALL ON public.system_config TO service_role;
GRANT ALL ON public.operations_audit_log TO service_role;
GRANT ALL ON public.admin_game_assignments TO service_role;
GRANT ALL ON public.admin_impersonation_sessions TO service_role;

-- Permission catalog additions for the operations console.
INSERT INTO public.admin_permissions (name, resource, action, description)
VALUES
  ('system:config_view', 'system', 'config_view', 'View global operations configuration'),
  ('system:config_edit', 'system', 'config_edit', 'Edit non-sensitive global operations configuration'),
  ('system:kill-switch', 'system', 'kill-switch', 'Toggle platform kill switches and emergency controls'),
  ('operations:audit', 'operations', 'audit', 'View and create structural operations audit events'),
  ('operations:abac_assign', 'operations', 'abac_assign', 'Assign contextual ABAC scopes such as game ownership')
ON CONFLICT (name) DO NOTHING;

INSERT INTO public.admin_role_permissions (role_id, permission_id)
SELECT ar.id, ap.id
FROM public.admin_roles ar
CROSS JOIN public.admin_permissions ap
WHERE ar.key = 'super_admin'
  AND ap.name IN (
    'system:config_view',
    'system:config_edit',
    'system:kill-switch',
    'operations:audit',
    'operations:abac_assign',
    'impersonation:start',
    'impersonation:stop',
    'impersonation:audit'
  )
ON CONFLICT DO NOTHING;
