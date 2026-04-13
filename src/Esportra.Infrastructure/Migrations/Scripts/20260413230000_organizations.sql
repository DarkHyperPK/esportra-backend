-- ============================================================================
-- Phase 11: Organizations — Multi-Venue Grouping
-- ============================================================================
-- Adds subscription_tier + settings to organizations table,
-- links venues to organizations via organization_id FK,
-- and ensures proper RLS + GRANTs.
-- ============================================================================

-- ── 1. Extend organizations table ───────────────────────────────────────────

ALTER TABLE organizations
  ADD COLUMN IF NOT EXISTS subscription_tier TEXT DEFAULT 'free';

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'organizations_subscription_tier_check'
  ) THEN
    ALTER TABLE organizations
      ADD CONSTRAINT organizations_subscription_tier_check
      CHECK (subscription_tier IN ('free', 'basic', 'pro', 'enterprise'));
  END IF;
END; $$;

ALTER TABLE organizations
  ADD COLUMN IF NOT EXISTS settings JSONB DEFAULT '{}'::jsonb;

-- ── 2. Link venues to organizations ─────────────────────────────────────────

ALTER TABLE venues
  ADD COLUMN IF NOT EXISTS organization_id UUID REFERENCES organizations(id);

CREATE INDEX IF NOT EXISTS idx_venues_organization
  ON venues(organization_id) WHERE organization_id IS NOT NULL;

-- ── 3. RLS on organizations ─────────────────────────────────────────────────

ALTER TABLE organizations ENABLE ROW LEVEL SECURITY;

-- Public read (org profiles are public)
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'organizations_public_read' AND tablename = 'organizations'
  ) THEN
    CREATE POLICY "organizations_public_read" ON organizations
      FOR SELECT USING (true);
  END IF;
END; $$;

-- Owner can insert their own orgs
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'organizations_owner_insert' AND tablename = 'organizations'
  ) THEN
    CREATE POLICY "organizations_owner_insert" ON organizations
      FOR INSERT WITH CHECK (auth.uid() = owner_id);
  END IF;
END; $$;

-- Owner can update their own orgs
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'organizations_owner_update' AND tablename = 'organizations'
  ) THEN
    CREATE POLICY "organizations_owner_update" ON organizations
      FOR UPDATE USING (auth.uid() = owner_id)
      WITH CHECK (auth.uid() = owner_id);
  END IF;
END; $$;

-- Owner can delete their own orgs
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'organizations_owner_delete' AND tablename = 'organizations'
  ) THEN
    CREATE POLICY "organizations_owner_delete" ON organizations
      FOR DELETE USING (auth.uid() = owner_id);
  END IF;
END; $$;

-- ── 4. GRANTs ───────────────────────────────────────────────────────────────

GRANT SELECT ON organizations TO authenticated;
GRANT INSERT, UPDATE, DELETE ON organizations TO authenticated;
GRANT ALL ON organizations TO service_role;

GRANT SELECT, UPDATE ON venues TO authenticated;
GRANT ALL ON venues TO service_role;
