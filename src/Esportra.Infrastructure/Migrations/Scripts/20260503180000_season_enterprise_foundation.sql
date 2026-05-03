-- ============================================================================
-- Migration: Season Enterprise Foundation
-- Purpose:   Add enterprise-grade season system tables for multi-tournament
--            competition management with proper audit trails, standings,
--            advancement tracking, and season-tournament mappings.
--
-- This migration implements the data model from the season plan:
-- - Extended seasons table with lifecycle timestamps and soft delete
-- - season_tournaments mapping table with roles
-- - Extended tournaments table with season ownership tracking
-- - season_advancement_records for tracking actual team movement
-- - season_standings for season-wide rankings
-- - season_audit_logs for append-only audit trail
-- ============================================================================

-- ── 1. Extend seasons table with enterprise columns ─────────────────────────
ALTER TABLE seasons
    ADD COLUMN IF NOT EXISTS visibility           TEXT NOT NULL DEFAULT 'private'
        CHECK (visibility IN ('private', 'unlisted', 'public')),
    ADD COLUMN IF NOT EXISTS published_at         TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS completed_at         TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS archived_at          TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS cancelled_at         TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS deleted_at           TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS version              INT NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS created_by           UUID NULL REFERENCES profiles(id) ON DELETE SET NULL;

-- Update status check to include 'cancelled'
DO $$ BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'seasons_status_check'
    ) THEN
        ALTER TABLE seasons DROP CONSTRAINT seasons_status_check;
    END IF;
END $$;

ALTER TABLE seasons
    ADD CONSTRAINT seasons_status_check
    CHECK (status IN ('draft', 'published', 'active', 'completed', 'archived', 'cancelled'));

-- Add indexes for enterprise columns
CREATE INDEX IF NOT EXISTS idx_seasons_visibility
    ON seasons (visibility);

CREATE INDEX IF NOT EXISTS idx_seasons_published_at
    ON seasons (published_at DESC)
    WHERE published_at IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_seasons_deleted_at
    ON seasons (deleted_at)
    WHERE deleted_at IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_seasons_start_date
    ON seasons (start_date)
    WHERE start_date IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_seasons_created_by
    ON seasons (created_by)
    WHERE created_by IS NOT NULL;

-- Update slug unique constraint to respect soft delete
DO $$ BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'seasons_slug_key'
    ) THEN
        ALTER TABLE seasons DROP CONSTRAINT seasons_slug_key;
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS uq_seasons_slug
    ON seasons (slug)
    WHERE deleted_at IS NULL;

-- ── 2. Create season_tournaments table ────────────────────────────────────────
CREATE TABLE IF NOT EXISTS season_tournaments (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id       UUID NOT NULL REFERENCES seasons(id) ON DELETE CASCADE,
    tournament_id   UUID NOT NULL REFERENCES tournaments(id) ON DELETE RESTRICT,
    role            TEXT NOT NULL
                    CHECK (role IN ('qualifier', 'event', 'regional_final', 'last_chance_qualifier', 'playoff', 'grand_final', 'custom')),
    region          TEXT NULL,
    display_name    TEXT NULL,
    sort_order      INT NOT NULL DEFAULT 0,
    status          TEXT NOT NULL DEFAULT 'draft'
                    CHECK (status IN ('draft', 'scheduled', 'live', 'completed', 'cancelled')),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Indexes for season_tournaments
CREATE INDEX IF NOT EXISTS idx_season_tournaments_season
    ON season_tournaments (season_id);

CREATE INDEX IF NOT EXISTS idx_season_tournaments_tournament
    ON season_tournaments (tournament_id);

CREATE INDEX IF NOT EXISTS idx_season_tournaments_season_role
    ON season_tournaments (season_id, role);

CREATE INDEX IF NOT EXISTS idx_season_tournaments_season_order
    ON season_tournaments (season_id, sort_order);

-- Ensure only one grand_final per season
CREATE UNIQUE INDEX IF NOT EXISTS uq_season_tournaments_grand_final
    ON season_tournaments (season_id)
    WHERE role = 'grand_final';

-- RLS for season_tournaments
ALTER TABLE season_tournaments ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'season_tournaments' AND policyname = 'season_tournaments_season_owner_select'
    ) THEN
        CREATE POLICY season_tournaments_season_owner_select ON season_tournaments
            FOR SELECT TO authenticated
            USING (
                EXISTS (
                    SELECT 1 FROM seasons s
                    WHERE s.id = season_tournaments.season_id
                      AND (s.owner_user_id = auth.uid() OR EXISTS (
                          SELECT 1 FROM season_staff ss
                          WHERE ss.season_id = s.id AND ss.user_id = auth.uid()
                      ))
                )
            );
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'season_tournaments' AND policyname = 'season_tournaments_season_owner_modify'
    ) THEN
        CREATE POLICY season_tournaments_season_owner_modify ON season_tournaments
            FOR ALL TO authenticated
            USING (
                EXISTS (
                    SELECT 1 FROM seasons s
                    WHERE s.id = season_tournaments.season_id
                      AND (s.owner_user_id = auth.uid() OR EXISTS (
                          SELECT 1 FROM season_staff ss
                          WHERE ss.season_id = s.id AND ss.user_id = auth.uid() AND ss.role = 'admin'
                      ))
                )
            )
            WITH CHECK (
                EXISTS (
                    SELECT 1 FROM seasons s
                    WHERE s.id = season_tournaments.season_id
                      AND (s.owner_user_id = auth.uid() OR EXISTS (
                          SELECT 1 FROM season_staff ss
                          WHERE ss.season_id = s.id AND ss.user_id = auth.uid() AND ss.role = 'admin'
                      ))
                )
            );
    END IF;
END $$;

GRANT ALL ON season_tournaments TO service_role;
GRANT SELECT ON season_tournaments TO authenticated;

-- ── 3. Extend tournaments table with season ownership ─────────────────────────
ALTER TABLE tournaments
    ADD COLUMN IF NOT EXISTS season_id      UUID NULL REFERENCES seasons(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS season_role    TEXT NULL
                    CHECK (season_role IN ('qualifier', 'event', 'regional_final', 'last_chance_qualifier', 'playoff', 'grand_final', 'custom')),
    ADD COLUMN IF NOT EXISTS created_via    TEXT NOT NULL DEFAULT 'standalone'
                    CHECK (created_via IN ('standalone', 'season', 'admin'));

-- Add index for season_id
CREATE INDEX IF NOT EXISTS idx_tournaments_season_id
    ON tournaments (season_id)
    WHERE season_id IS NOT NULL;

-- Backfill existing tournaments with created_via = 'standalone'
UPDATE tournaments
SET created_via = 'standalone'
WHERE created_via IS NULL OR created_via NOT IN ('standalone', 'season', 'admin');

-- ── 4. Add status column to season_advancement_connections (if missing) ───────
DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'season_advancement_connections' AND column_name = 'status'
    ) THEN
        ALTER TABLE season_advancement_connections
            ADD COLUMN status TEXT NOT NULL DEFAULT 'pending'
                CHECK (status IN ('pending', 'active', 'disabled'));
        
        CREATE INDEX IF NOT EXISTS idx_sac_season_from_status
            ON season_advancement_connections (season_id, from_node_id, status);
    END IF;
END $$;

-- ── 5. Create season_advancement_records table ────────────────────────────────
CREATE TABLE IF NOT EXISTS season_advancement_records (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id           UUID NOT NULL REFERENCES seasons(id) ON DELETE CASCADE,
    connection_id       UUID NOT NULL REFERENCES season_advancement_connections(id) ON DELETE CASCADE,
    from_tournament_id  UUID NOT NULL REFERENCES tournaments(id) ON DELETE RESTRICT,
    to_tournament_id    UUID NOT NULL REFERENCES tournaments(id) ON DELETE RESTRICT,
    team_id             UUID NOT NULL REFERENCES teams(id) ON DELETE RESTRICT,
    source_rank         INT NULL,
    target_seed         INT NULL,
    status              TEXT NOT NULL DEFAULT 'pending'
                        CHECK (status IN ('pending', 'advanced', 'blocked', 'removed', 'manual_override')),
    advanced_at         TIMESTAMPTZ NULL,
    advanced_by         UUID NULL REFERENCES profiles(id) ON DELETE SET NULL,
    reason              TEXT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Indexes for season_advancement_records
CREATE INDEX IF NOT EXISTS idx_sar_season
    ON season_advancement_records (season_id);

CREATE INDEX IF NOT EXISTS idx_sar_connection
    ON season_advancement_records (connection_id);

CREATE INDEX IF NOT EXISTS idx_sar_team
    ON season_advancement_records (team_id);

CREATE INDEX IF NOT EXISTS idx_sar_from_tournament
    ON season_advancement_records (from_tournament_id);

CREATE INDEX IF NOT EXISTS idx_sar_to_tournament
    ON season_advancement_records (to_tournament_id);

-- Unique constraint to prevent duplicate advancements
CREATE UNIQUE INDEX IF NOT EXISTS uq_sar_connection_team
    ON season_advancement_records (connection_id, team_id);

-- RLS for season_advancement_records
ALTER TABLE season_advancement_records ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'season_advancement_records' AND policyname = 'season_advancement_records_season_owner_select'
    ) THEN
        CREATE POLICY season_advancement_records_season_owner_select ON season_advancement_records
            FOR SELECT TO authenticated
            USING (
                EXISTS (
                    SELECT 1 FROM seasons s
                    WHERE s.id = season_advancement_records.season_id
                      AND (s.owner_user_id = auth.uid() OR EXISTS (
                          SELECT 1 FROM season_staff ss
                          WHERE ss.season_id = s.id AND ss.user_id = auth.uid()
                      ))
                )
            );
    END IF;
END $$;

GRANT ALL ON season_advancement_records TO service_role;
GRANT SELECT ON season_advancement_records TO authenticated;

-- ── 6. Create season_standings table ──────────────────────────────────────────
CREATE TABLE IF NOT EXISTS season_standings (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id           UUID NOT NULL REFERENCES seasons(id) ON DELETE CASCADE,
    team_id             UUID NOT NULL REFERENCES teams(id) ON DELETE RESTRICT,
    total_points        INT NOT NULL DEFAULT 0,
    tournaments_played  INT NOT NULL DEFAULT 0,
    best_finish         INT NULL,
    current_status      TEXT NOT NULL DEFAULT 'registered'
                        CHECK (current_status IN ('registered', 'active', 'qualified', 'eliminated', 'champion', 'disqualified')),
    last_tournament_id  UUID NULL REFERENCES tournaments(id) ON DELETE SET NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Indexes for season_standings
CREATE INDEX IF NOT EXISTS idx_ss_season_points
    ON season_standings (season_id, total_points DESC);

CREATE INDEX IF NOT EXISTS idx_ss_season_team
    ON season_standings (season_id, team_id);

CREATE INDEX IF NOT EXISTS idx_ss_team
    ON season_standings (team_id);

-- Unique constraint: one standing per team per season
CREATE UNIQUE INDEX IF NOT EXISTS uq_ss_season_team
    ON season_standings (season_id, team_id);

-- RLS for season_standings
ALTER TABLE season_standings ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'season_standings' AND policyname = 'season_standings_public_select'
    ) THEN
        CREATE POLICY season_standings_public_select ON season_standings
            FOR SELECT TO authenticated
            USING (
                EXISTS (
                    SELECT 1 FROM seasons s
                    WHERE s.id = season_standings.season_id AND s.is_public = TRUE
                )
            );
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'season_standings' AND policyname = 'season_standings_season_owner_select'
    ) THEN
        CREATE POLICY season_standings_season_owner_select ON season_standings
            FOR SELECT TO authenticated
            USING (
                EXISTS (
                    SELECT 1 FROM seasons s
                    WHERE s.id = season_standings.season_id
                      AND (s.owner_user_id = auth.uid() OR EXISTS (
                          SELECT 1 FROM season_staff ss
                          WHERE ss.season_id = s.id AND ss.user_id = auth.uid()
                      ))
                )
            );
    END IF;
END $$;

GRANT ALL ON season_standings TO service_role;
GRANT SELECT ON season_standings TO authenticated;

-- ── 7. Create season_audit_logs table (append-only) ───────────────────────────
CREATE TABLE IF NOT EXISTS season_audit_logs (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id       UUID NOT NULL REFERENCES seasons(id) ON DELETE CASCADE,
    actor_id        UUID NOT NULL REFERENCES profiles(id) ON DELETE RESTRICT,
    action          TEXT NOT NULL
                    CHECK (action IN (
                        'season_created', 'season_updated', 'season_published', 'season_archived',
                        'season_cancelled', 'tournament_created', 'tournament_updated',
                        'advancement_rule_created', 'advancement_rule_changed', 'team_advanced',
                        'team_removed', 'manual_override', 'stage_deleted', 'staff_changed',
                        'permission_changed'
                    )),
    entity_type     TEXT NOT NULL,
    entity_id       UUID NULL,
    before          JSONB NULL,
    after           JSONB NULL,
    reason          TEXT NULL,
    ip_address      TEXT NULL,
    user_agent      TEXT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Indexes for season_audit_logs
CREATE INDEX IF NOT EXISTS idx_sal_season_created
    ON season_audit_logs (season_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_sal_actor_created
    ON season_audit_logs (actor_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_sal_entity
    ON season_audit_logs (entity_type, entity_id);

-- RLS for season_audit_logs (append-only)
ALTER TABLE season_audit_logs ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'season_audit_logs' AND policyname = 'season_audit_logs_season_owner_select'
    ) THEN
        CREATE POLICY season_audit_logs_season_owner_select ON season_audit_logs
            FOR SELECT TO authenticated
            USING (
                EXISTS (
                    SELECT 1 FROM seasons s
                    WHERE s.id = season_audit_logs.season_id
                      AND (s.owner_user_id = auth.uid() OR EXISTS (
                          SELECT 1 FROM season_staff ss
                          WHERE ss.season_id = s.id AND ss.user_id = auth.uid() AND ss.role = 'admin'
                      ))
                )
            );
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'season_audit_logs' AND policyname = 'season_audit_logs_service_insert'
    ) THEN
        CREATE POLICY season_audit_logs_service_insert ON season_audit_logs
            FOR INSERT TO service_role
            WITH CHECK (TRUE);
    END IF;
END $$;

-- No UPDATE or DELETE policies (append-only enforcement)
GRANT ALL ON season_audit_logs TO service_role;
GRANT SELECT ON season_audit_logs TO authenticated;

-- ============================================================================
-- Post-conditions:
--   - seasons table extended with lifecycle timestamps and soft delete support
--   - season_tournaments mapping table created for role-based tournament tracking
--   - tournaments table extended with season ownership tracking
--   - season_advancement_records table created for tracking actual team movement
--   - season_standings table created for season-wide rankings
--   - season_audit_logs table created for append-only audit trail
--   - All tables have proper indexes, constraints, and RLS policies
-- ============================================================================
