-- Phase 4: Members — venue-specific customer records
-- Walk-ins don't need Supabase auth; linked members get loyalty, balance, history.

CREATE TABLE IF NOT EXISTS members (
  id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  venue_id        UUID NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
  user_id         UUID REFERENCES auth.users(id),  -- nullable: walk-ins have no auth account
  display_name    TEXT NOT NULL,
  email           TEXT,
  phone           TEXT,
  avatar_url      TEXT,
  balance         NUMERIC(10,2) NOT NULL DEFAULT 0,
  loyalty_points  INT NOT NULL DEFAULT 0,
  loyalty_tier    TEXT NOT NULL DEFAULT 'bronze'
    CHECK (loyalty_tier IN ('bronze', 'silver', 'gold', 'platinum')),
  total_hours     NUMERIC(10,1) NOT NULL DEFAULT 0,
  total_spent     NUMERIC(10,2) NOT NULL DEFAULT 0,
  total_sessions  INT NOT NULL DEFAULT 0,
  is_banned       BOOLEAN NOT NULL DEFAULT false,
  ban_reason      TEXT,
  banned_at       TIMESTAMPTZ,
  notes           TEXT,
  date_of_birth   DATE,
  created_at      TIMESTAMPTZ DEFAULT NOW(),
  updated_at      TIMESTAMPTZ DEFAULT NOW()
);

-- One member per auth user per venue (but allow multiple null user_id for walk-ins)
CREATE UNIQUE INDEX IF NOT EXISTS idx_members_user_venue
  ON members(user_id, venue_id) WHERE user_id IS NOT NULL;

-- Fast search by venue + name/email
CREATE INDEX IF NOT EXISTS idx_members_venue ON members(venue_id);
CREATE INDEX IF NOT EXISTS idx_members_search
  ON members(venue_id, display_name, email);

ALTER TABLE members ENABLE ROW LEVEL SECURITY;

-- Service role full access
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'members_service_all' AND tablename = 'members') THEN
    CREATE POLICY members_service_all ON members
      FOR ALL USING (current_setting('request.jwt.claim.role', true) = 'service_role');
  END IF;
END $$;

-- Venue owner/staff can read their venue's members
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'members_staff_select' AND tablename = 'members') THEN
    CREATE POLICY members_staff_select ON members
      FOR SELECT USING (
        EXISTS (
          SELECT 1 FROM venues WHERE venues.id = members.venue_id AND venues.owner_id = auth.uid()
        )
        OR EXISTS (
          SELECT 1 FROM venue_staff WHERE venue_staff.venue_id = members.venue_id AND venue_staff.user_id = auth.uid() AND venue_staff.status = 'active'
        )
      );
  END IF;
END $$;

-- Users can view their own member profiles
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'members_user_select' AND tablename = 'members') THEN
    CREATE POLICY members_user_select ON members
      FOR SELECT USING (auth.uid() = user_id);
  END IF;
END $$;

-- GRANTs
GRANT ALL ON members TO service_role;
GRANT SELECT ON members TO authenticated;

-- Add member_id FK to venue_sessions if the column exists but has no FK
-- (column was added in 20260413130100 without FK since members didn't exist yet)
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.table_constraints tc
    JOIN information_schema.constraint_column_usage ccu ON tc.constraint_name = ccu.constraint_name
    WHERE tc.table_name = 'venue_sessions' AND ccu.column_name = 'member_id' AND tc.constraint_type = 'FOREIGN KEY'
  ) THEN
    ALTER TABLE venue_sessions
      ADD CONSTRAINT fk_venue_sessions_member FOREIGN KEY (member_id) REFERENCES members(id) ON DELETE SET NULL;
  END IF;
END $$;

-- Add member_id to session_invoices FK
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.table_constraints tc
    JOIN information_schema.constraint_column_usage ccu ON tc.constraint_name = ccu.constraint_name
    WHERE tc.table_name = 'session_invoices' AND ccu.column_name = 'member_id' AND tc.constraint_type = 'FOREIGN KEY'
  ) THEN
    ALTER TABLE session_invoices
      ADD CONSTRAINT fk_session_invoices_member FOREIGN KEY (member_id) REFERENCES members(id) ON DELETE SET NULL;
  END IF;
END $$;
