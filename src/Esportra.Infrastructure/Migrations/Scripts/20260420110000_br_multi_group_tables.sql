-- ============================================================================
-- Migration: Battle Royale Multi-Group Stage Tables
-- Purpose:   Structured group/round/result tables for BR tournament stages,
--            replacing the monolithic br_game_data JSONB approach.
-- ============================================================================

-- ── 1. br_groups ────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS br_groups (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    stage_id        UUID        NOT NULL REFERENCES tournament_stages(id) ON DELETE CASCADE,
    name            TEXT        NOT NULL DEFAULT 'Group A',
    group_order     INT         NOT NULL DEFAULT 0,
    lobby_size      INT         NOT NULL DEFAULT 20,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),

    UNIQUE (stage_id, group_order)
);

CREATE INDEX IF NOT EXISTS idx_br_groups_stage_id
    ON br_groups (stage_id);

-- ── 2. br_group_teams ───────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS br_group_teams (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    group_id        UUID        NOT NULL REFERENCES br_groups(id) ON DELETE CASCADE,
    team_id         UUID        NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    seed_order      INT         NOT NULL DEFAULT 0,
    assigned_at     TIMESTAMPTZ NOT NULL DEFAULT now(),

    UNIQUE (group_id, team_id)
);

CREATE INDEX IF NOT EXISTS idx_br_group_teams_team_id
    ON br_group_teams (team_id);

-- ── 3. br_rounds ────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS br_rounds (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    group_id        UUID        NOT NULL REFERENCES br_groups(id) ON DELETE CASCADE,
    round_number    INT         NOT NULL,
    lobby_code      TEXT,
    status          TEXT        NOT NULL DEFAULT 'pending'
                        CHECK (status IN ('pending', 'active', 'completed')),
    scheduled_at    TIMESTAMPTZ,
    started_at      TIMESTAMPTZ,
    completed_at    TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),

    UNIQUE (group_id, round_number)
);

CREATE INDEX IF NOT EXISTS idx_br_rounds_group_id
    ON br_rounds (group_id);

-- ── 4. br_round_results ─────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS br_round_results (
    id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    round_id            UUID        NOT NULL REFERENCES br_rounds(id) ON DELETE CASCADE,
    team_id             UUID        NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    placement           INT         NOT NULL CHECK (placement >= 1),
    kills               INT         NOT NULL DEFAULT 0 CHECK (kills >= 0),
    placement_points    INT         NOT NULL DEFAULT 0,
    kill_points         INT         NOT NULL DEFAULT 0,
    total_points        INT         NOT NULL GENERATED ALWAYS AS (placement_points + kill_points) STORED,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),

    UNIQUE (round_id, team_id)
);

CREATE INDEX IF NOT EXISTS idx_br_round_results_round_id
    ON br_round_results (round_id);

CREATE INDEX IF NOT EXISTS idx_br_round_results_team_id
    ON br_round_results (team_id);

-- ── RLS ─────────────────────────────────────────────────────────────────────

ALTER TABLE br_groups         ENABLE ROW LEVEL SECURITY;
ALTER TABLE br_group_teams    ENABLE ROW LEVEL SECURITY;
ALTER TABLE br_rounds         ENABLE ROW LEVEL SECURITY;
ALTER TABLE br_round_results  ENABLE ROW LEVEL SECURITY;

-- ── RLS Policies: br_groups ─────────────────────────────────────────────────

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_groups' AND policyname = 'br_groups_authenticated_select'
    ) THEN
        CREATE POLICY br_groups_authenticated_select ON br_groups
            FOR SELECT TO authenticated USING (true);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_groups' AND policyname = 'br_groups_authenticated_insert'
    ) THEN
        CREATE POLICY br_groups_authenticated_insert ON br_groups
            FOR INSERT TO authenticated WITH CHECK (true);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_groups' AND policyname = 'br_groups_authenticated_update'
    ) THEN
        CREATE POLICY br_groups_authenticated_update ON br_groups
            FOR UPDATE TO authenticated USING (true) WITH CHECK (true);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_groups' AND policyname = 'br_groups_authenticated_delete'
    ) THEN
        CREATE POLICY br_groups_authenticated_delete ON br_groups
            FOR DELETE TO authenticated USING (true);
    END IF;
END $$;

-- ── RLS Policies: br_group_teams ────────────────────────────────────────────

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_group_teams' AND policyname = 'br_group_teams_authenticated_select'
    ) THEN
        CREATE POLICY br_group_teams_authenticated_select ON br_group_teams
            FOR SELECT TO authenticated USING (true);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_group_teams' AND policyname = 'br_group_teams_authenticated_insert'
    ) THEN
        CREATE POLICY br_group_teams_authenticated_insert ON br_group_teams
            FOR INSERT TO authenticated WITH CHECK (true);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_group_teams' AND policyname = 'br_group_teams_authenticated_update'
    ) THEN
        CREATE POLICY br_group_teams_authenticated_update ON br_group_teams
            FOR UPDATE TO authenticated USING (true) WITH CHECK (true);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_group_teams' AND policyname = 'br_group_teams_authenticated_delete'
    ) THEN
        CREATE POLICY br_group_teams_authenticated_delete ON br_group_teams
            FOR DELETE TO authenticated USING (true);
    END IF;
END $$;

-- ── RLS Policies: br_rounds ────────────────────────────────────────────────

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_rounds' AND policyname = 'br_rounds_authenticated_select'
    ) THEN
        CREATE POLICY br_rounds_authenticated_select ON br_rounds
            FOR SELECT TO authenticated USING (true);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_rounds' AND policyname = 'br_rounds_authenticated_insert'
    ) THEN
        CREATE POLICY br_rounds_authenticated_insert ON br_rounds
            FOR INSERT TO authenticated WITH CHECK (true);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_rounds' AND policyname = 'br_rounds_authenticated_update'
    ) THEN
        CREATE POLICY br_rounds_authenticated_update ON br_rounds
            FOR UPDATE TO authenticated USING (true) WITH CHECK (true);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_rounds' AND policyname = 'br_rounds_authenticated_delete'
    ) THEN
        CREATE POLICY br_rounds_authenticated_delete ON br_rounds
            FOR DELETE TO authenticated USING (true);
    END IF;
END $$;

-- ── RLS Policies: br_round_results ──────────────────────────────────────────

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_round_results' AND policyname = 'br_round_results_authenticated_select'
    ) THEN
        CREATE POLICY br_round_results_authenticated_select ON br_round_results
            FOR SELECT TO authenticated USING (true);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_round_results' AND policyname = 'br_round_results_authenticated_insert'
    ) THEN
        CREATE POLICY br_round_results_authenticated_insert ON br_round_results
            FOR INSERT TO authenticated WITH CHECK (true);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_round_results' AND policyname = 'br_round_results_authenticated_update'
    ) THEN
        CREATE POLICY br_round_results_authenticated_update ON br_round_results
            FOR UPDATE TO authenticated USING (true) WITH CHECK (true);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_round_results' AND policyname = 'br_round_results_authenticated_delete'
    ) THEN
        CREATE POLICY br_round_results_authenticated_delete ON br_round_results
            FOR DELETE TO authenticated USING (true);
    END IF;
END $$;

-- ── Grants ──────────────────────────────────────────────────────────────────

GRANT ALL ON br_groups        TO service_role;
GRANT ALL ON br_group_teams   TO service_role;
GRANT ALL ON br_rounds        TO service_role;
GRANT ALL ON br_round_results TO service_role;

GRANT SELECT, INSERT, UPDATE, DELETE ON br_groups        TO authenticated;
GRANT SELECT, INSERT, UPDATE, DELETE ON br_group_teams   TO authenticated;
GRANT SELECT, INSERT, UPDATE, DELETE ON br_rounds        TO authenticated;
GRANT SELECT, INSERT, UPDATE, DELETE ON br_round_results TO authenticated;
