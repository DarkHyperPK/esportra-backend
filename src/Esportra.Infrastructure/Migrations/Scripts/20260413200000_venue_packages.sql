-- ============================================================================
-- Migration: 20260413200000_venue_packages.sql
-- Purpose:   Hour packages system — venues sell hour bundles, members purchase
--            and consume them.
-- Tables:    venue_packages, member_packages
-- ============================================================================

-- ── 1. venue_packages — hour bundles that venues offer for sale ─────────────
CREATE TABLE IF NOT EXISTS venue_packages (
    id              UUID            PRIMARY KEY DEFAULT gen_random_uuid(),
    venue_id        UUID            NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
    name            TEXT            NOT NULL,
    description     TEXT            DEFAULT '',
    hours           NUMERIC(6,1)    NOT NULL CHECK (hours > 0),
    price           NUMERIC(10,2)   NOT NULL CHECK (price >= 0),
    original_price  NUMERIC(10,2)   CHECK (original_price IS NULL OR original_price >= 0),
    validity_days   INT             NOT NULL DEFAULT 30 CHECK (validity_days > 0),
    is_active       BOOLEAN         NOT NULL DEFAULT true,
    sort_order      INT             NOT NULL DEFAULT 0,
    created_at      TIMESTAMPTZ     NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ     NOT NULL DEFAULT now()
);

-- ── 2. member_packages — purchased packages linked to members ──────────────
CREATE TABLE IF NOT EXISTS member_packages (
    id              UUID            PRIMARY KEY DEFAULT gen_random_uuid(),
    venue_id        UUID            NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
    member_id       UUID            NOT NULL REFERENCES members(id) ON DELETE CASCADE,
    package_id      UUID            NOT NULL REFERENCES venue_packages(id),
    hours_total     NUMERIC(6,1)    NOT NULL CHECK (hours_total > 0),
    hours_remaining NUMERIC(6,1)    NOT NULL CHECK (hours_remaining >= 0),
    purchased_at    TIMESTAMPTZ     NOT NULL DEFAULT now(),
    expires_at      TIMESTAMPTZ     NOT NULL,
    status          TEXT            NOT NULL DEFAULT 'active'
                                    CHECK (status IN ('active', 'expired', 'depleted')),
    created_at      TIMESTAMPTZ     NOT NULL DEFAULT now()
);

-- ── 3. Indexes ─────────────────────────────────────────────────────────────
CREATE INDEX IF NOT EXISTS idx_venue_packages_venue_id
    ON venue_packages (venue_id);

CREATE INDEX IF NOT EXISTS idx_venue_packages_venue_active
    ON venue_packages (venue_id, is_active)
    WHERE is_active = true;

CREATE INDEX IF NOT EXISTS idx_member_packages_venue_member
    ON member_packages (venue_id, member_id);

CREATE INDEX IF NOT EXISTS idx_member_packages_status
    ON member_packages (venue_id, member_id, status)
    WHERE status = 'active';

CREATE INDEX IF NOT EXISTS idx_member_packages_expires_at
    ON member_packages (expires_at)
    WHERE status = 'active';

-- ── 4. updated_at trigger for venue_packages ───────────────────────────────
DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'trg_venue_packages_updated_at'
    ) THEN
        CREATE TRIGGER trg_venue_packages_updated_at
            BEFORE UPDATE ON venue_packages
            FOR EACH ROW
            EXECUTE FUNCTION update_updated_at_column();
    END IF;
END; $$;

-- ── 5. RLS ─────────────────────────────────────────────────────────────────
ALTER TABLE venue_packages ENABLE ROW LEVEL SECURITY;
ALTER TABLE member_packages ENABLE ROW LEVEL SECURITY;

-- venue_packages: anyone can read active packages (public catalog)
DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies WHERE policyname = 'venue_packages_public_read' AND tablename = 'venue_packages'
    ) THEN
        CREATE POLICY "venue_packages_public_read" ON venue_packages
            FOR SELECT USING (true);
    END IF;
END; $$;

-- venue_packages: owner/staff can insert
DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies WHERE policyname = 'venue_packages_owner_staff_insert' AND tablename = 'venue_packages'
    ) THEN
        CREATE POLICY "venue_packages_owner_staff_insert" ON venue_packages
            FOR INSERT WITH CHECK (
                EXISTS (
                    SELECT 1 FROM venues WHERE venues.id = venue_packages.venue_id AND venues.owner_id = auth.uid()
                )
                OR EXISTS (
                    SELECT 1 FROM venue_staff vs WHERE vs.venue_id = venue_packages.venue_id AND vs.user_id = auth.uid() AND vs.status = 'active'
                )
            );
    END IF;
END; $$;

-- venue_packages: owner/staff can update
DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies WHERE policyname = 'venue_packages_owner_staff_update' AND tablename = 'venue_packages'
    ) THEN
        CREATE POLICY "venue_packages_owner_staff_update" ON venue_packages
            FOR UPDATE USING (
                EXISTS (
                    SELECT 1 FROM venues WHERE venues.id = venue_packages.venue_id AND venues.owner_id = auth.uid()
                )
                OR EXISTS (
                    SELECT 1 FROM venue_staff vs WHERE vs.venue_id = venue_packages.venue_id AND vs.user_id = auth.uid() AND vs.status = 'active'
                )
            );
    END IF;
END; $$;

-- member_packages: owner/staff can read all for their venue
DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies WHERE policyname = 'member_packages_owner_staff_read' AND tablename = 'member_packages'
    ) THEN
        CREATE POLICY "member_packages_owner_staff_read" ON member_packages
            FOR SELECT USING (
                EXISTS (
                    SELECT 1 FROM venues WHERE venues.id = member_packages.venue_id AND venues.owner_id = auth.uid()
                )
                OR EXISTS (
                    SELECT 1 FROM venue_staff vs WHERE vs.venue_id = member_packages.venue_id AND vs.user_id = auth.uid() AND vs.status = 'active'
                )
            );
    END IF;
END; $$;

-- member_packages: owner/staff can insert (purchase on behalf)
DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies WHERE policyname = 'member_packages_owner_staff_insert' AND tablename = 'member_packages'
    ) THEN
        CREATE POLICY "member_packages_owner_staff_insert" ON member_packages
            FOR INSERT WITH CHECK (
                EXISTS (
                    SELECT 1 FROM venues WHERE venues.id = member_packages.venue_id AND venues.owner_id = auth.uid()
                )
                OR EXISTS (
                    SELECT 1 FROM venue_staff vs WHERE vs.venue_id = member_packages.venue_id AND vs.user_id = auth.uid() AND vs.status = 'active'
                )
            );
    END IF;
END; $$;

-- member_packages: owner/staff can update (deduct hours, change status)
DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies WHERE policyname = 'member_packages_owner_staff_update' AND tablename = 'member_packages'
    ) THEN
        CREATE POLICY "member_packages_owner_staff_update" ON member_packages
            FOR UPDATE USING (
                EXISTS (
                    SELECT 1 FROM venues WHERE venues.id = member_packages.venue_id AND venues.owner_id = auth.uid()
                )
                OR EXISTS (
                    SELECT 1 FROM venue_staff vs WHERE vs.venue_id = member_packages.venue_id AND vs.user_id = auth.uid() AND vs.status = 'active'
                )
            );
    END IF;
END; $$;

-- ── 6. GRANTs for service_role ─────────────────────────────────────────────
GRANT SELECT, INSERT, UPDATE, DELETE ON venue_packages TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON member_packages TO service_role;

-- GRANTs for authenticated role (RLS enforced)
GRANT SELECT, INSERT, UPDATE ON venue_packages TO authenticated;
GRANT SELECT, INSERT, UPDATE ON member_packages TO authenticated;
