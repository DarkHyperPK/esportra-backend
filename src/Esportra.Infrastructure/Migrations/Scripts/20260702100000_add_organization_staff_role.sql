-- Add role column to organization_staff table
-- The code references os.role for admin checks but the column was never added via migration

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name = 'organization_staff'
      AND column_name = 'role'
  ) THEN
    ALTER TABLE public.organization_staff
      ADD COLUMN role TEXT NOT NULL DEFAULT 'staff' CHECK (role IN ('owner', 'admin', 'staff'));
  END IF;
END $$;

-- Index for role lookups (org admin checks)
CREATE INDEX IF NOT EXISTS idx_organization_staff_role ON public.organization_staff(role);
