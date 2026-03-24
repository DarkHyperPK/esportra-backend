-- Ensure admin_roles table exists with all required columns.
CREATE TABLE IF NOT EXISTS public.admin_roles (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name        TEXT NOT NULL UNIQUE,
  key         TEXT UNIQUE,
  description TEXT,
  created_at  TIMESTAMPTZ DEFAULT now()
);

-- Add key column if it was missing from an older schema.
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name   = 'admin_roles'
      AND column_name  = 'key'
  ) THEN
    ALTER TABLE public.admin_roles ADD COLUMN key TEXT UNIQUE;
  END IF;
END;
$$;

-- Seed the four assignable admin roles (super_admin is never assigned via UI).
-- ON CONFLICT updates key + description so re-running is safe.
INSERT INTO public.admin_roles (name, key, description) VALUES
  ('ops_admin',     'ops_admin',     'Operations Admin — manages tournaments, venues, and platform users'),
  ('moderator',     'moderator',     'Moderator — handles disputes and content moderation'),
  ('finance_admin', 'finance_admin', 'Finance Admin — manages financial reports and billing'),
  ('support_admin', 'support_admin', 'Support Admin — handles user support and verification requests')
ON CONFLICT (name) DO UPDATE SET
  key         = EXCLUDED.key,
  description = EXCLUDED.description;

-- ── admin_user_roles ────────────────────────────────────────────────────────
-- Ensure the join table exists.
CREATE TABLE IF NOT EXISTS public.admin_user_roles (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id     UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
  role_id     UUID NOT NULL REFERENCES public.admin_roles(id) ON DELETE CASCADE,
  assigned_by UUID REFERENCES auth.users(id),
  assigned_at TIMESTAMPTZ DEFAULT now(),
  UNIQUE (user_id, role_id)
);

-- ── RLS ─────────────────────────────────────────────────────────────────────

-- admin_roles: role definitions are not sensitive — any authenticated user may
-- read them so the AdminAccess dropdown can populate.
ALTER TABLE public.admin_roles ENABLE ROW LEVEL SECURITY;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE schemaname = 'public'
      AND tablename  = 'admin_roles'
      AND policyname = 'admin_roles_select_authenticated'
  ) THEN
    CREATE POLICY admin_roles_select_authenticated ON public.admin_roles
      FOR SELECT TO authenticated USING (true);
  END IF;

  -- Only admins can mutate admin_roles rows.
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE schemaname = 'public'
      AND tablename  = 'admin_roles'
      AND policyname = 'admin_roles_write_admin_only'
  ) THEN
    CREATE POLICY admin_roles_write_admin_only ON public.admin_roles
      FOR ALL TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM public.profiles
          WHERE id = auth.uid() AND is_admin = true
        )
      )
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM public.profiles
          WHERE id = auth.uid() AND is_admin = true
        )
      );
  END IF;
END;
$$;

-- admin_user_roles: only admins can read or write assignments.
ALTER TABLE public.admin_user_roles ENABLE ROW LEVEL SECURITY;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE schemaname = 'public'
      AND tablename  = 'admin_user_roles'
      AND policyname = 'admin_user_roles_admin_all'
  ) THEN
    CREATE POLICY admin_user_roles_admin_all ON public.admin_user_roles
      FOR ALL TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM public.profiles
          WHERE id = auth.uid() AND is_admin = true
        )
      )
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM public.profiles
          WHERE id = auth.uid() AND is_admin = true
        )
      );
  END IF;
END;
$$;

GRANT SELECT ON public.admin_roles TO authenticated;
GRANT ALL    ON public.admin_user_roles TO authenticated;
