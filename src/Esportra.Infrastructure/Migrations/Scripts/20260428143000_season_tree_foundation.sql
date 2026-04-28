-- ============================================================================
-- Migration: Season Tree Foundation
-- Purpose:   Add generic season-tree support with linked tournament/stage nodes,
--            organizer-managed staff roles, qualification/points scaffolding,
--            branch-lock enforcement tables, and reusable profile nationality.
-- ============================================================================

-- ── Profiles: nationality is explicit but stored in a private table ───────────
CREATE TABLE IF NOT EXISTS profile_private_details (
    user_id      UUID PRIMARY KEY REFERENCES profiles(id) ON DELETE CASCADE,
    nationality  TEXT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_profile_private_details_nationality
    ON profile_private_details (nationality)
    WHERE nationality IS NOT NULL;

ALTER TABLE profile_private_details ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'profile_private_details'
          AND policyname = 'profile_private_details_self_select'
    ) THEN
        CREATE POLICY profile_private_details_self_select ON profile_private_details
            FOR SELECT TO authenticated
            USING (auth.uid() = user_id);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'profile_private_details'
          AND policyname = 'profile_private_details_self_insert'
    ) THEN
        CREATE POLICY profile_private_details_self_insert ON profile_private_details
            FOR INSERT TO authenticated
            WITH CHECK (auth.uid() = user_id);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'profile_private_details'
          AND policyname = 'profile_private_details_self_update'
    ) THEN
        CREATE POLICY profile_private_details_self_update ON profile_private_details
            FOR UPDATE TO authenticated
            USING (auth.uid() = user_id)
            WITH CHECK (auth.uid() = user_id);
    END IF;
END $$;

-- ── Seasons ──────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS seasons (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name                    TEXT NOT NULL,
    slug                    TEXT NOT NULL UNIQUE,
    description             TEXT NULL,
    game                    TEXT NOT NULL,
    participant_mode        TEXT NOT NULL
                                CHECK (participant_mode IN ('team', 'solo')),
    status                  TEXT NOT NULL DEFAULT 'draft'
                                CHECK (status IN ('draft', 'published', 'active', 'completed', 'archived')),
    owner_user_id           UUID NOT NULL REFERENCES profiles(id) ON DELETE RESTRICT,
    organization_id         UUID NULL REFERENCES organizations(id) ON DELETE SET NULL,
    is_public               BOOLEAN NOT NULL DEFAULT FALSE,
    allow_manual_overrides  BOOLEAN NOT NULL DEFAULT TRUE,
    start_date              TIMESTAMPTZ NULL,
    end_date                TIMESTAMPTZ NULL,
    settings                JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_seasons_owner_user_id
    ON seasons (owner_user_id);

CREATE INDEX IF NOT EXISTS idx_seasons_game_status
    ON seasons (game, status);

ALTER TABLE seasons ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'seasons' AND policyname = 'seasons_public_select'
    ) THEN
        CREATE POLICY seasons_public_select ON seasons
            FOR SELECT TO authenticated
            USING (is_public = TRUE);
    END IF;
END $$;

-- ── Season staff ─────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS season_staff (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id    UUID NOT NULL REFERENCES seasons(id) ON DELETE CASCADE,
    user_id      UUID NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    role         TEXT NOT NULL CHECK (role IN ('co_organizer', 'admin')),
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (season_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_season_staff_season_id
    ON season_staff (season_id);

CREATE INDEX IF NOT EXISTS idx_season_staff_user_id
    ON season_staff (user_id);

ALTER TABLE season_staff ENABLE ROW LEVEL SECURITY;

-- ── Season nodes (tree structure) ────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS season_nodes (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id               UUID NOT NULL REFERENCES seasons(id) ON DELETE CASCADE,
    parent_node_id          UUID NULL REFERENCES season_nodes(id) ON DELETE CASCADE,
    name                    TEXT NOT NULL,
    slug                    TEXT NULL,
    node_type               TEXT NOT NULL DEFAULT 'custom'
                                CHECK (node_type IN ('root', 'qualifier', 'event', 'stage', 'final', 'custom')),
    display_order           INT NOT NULL DEFAULT 0,
    region                  TEXT NULL,
    city                    TEXT NULL,
    country                 TEXT NULL,
    linked_tournament_id    UUID NULL REFERENCES tournaments(id) ON DELETE SET NULL,
    linked_stage_id         UUID NULL REFERENCES tournament_stages(id) ON DELETE SET NULL,
    status                  TEXT NOT NULL DEFAULT 'draft'
                                CHECK (status IN ('draft', 'scheduled', 'live', 'completed', 'archived')),
    registration_deadline   TIMESTAMPTZ NULL,
    starts_at               TIMESTAMPTZ NULL,
    ends_at                 TIMESTAMPTZ NULL,
    metadata                JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_season_nodes_season_order
    ON season_nodes (season_id, display_order, created_at);

CREATE INDEX IF NOT EXISTS idx_season_nodes_parent
    ON season_nodes (parent_node_id);

CREATE UNIQUE INDEX IF NOT EXISTS uq_season_nodes_season_id_id
    ON season_nodes (season_id, id);

CREATE UNIQUE INDEX IF NOT EXISTS uq_season_nodes_linked_tournament
    ON season_nodes (linked_tournament_id)
    WHERE linked_tournament_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_season_nodes_linked_stage
    ON season_nodes (linked_stage_id)
    WHERE linked_stage_id IS NOT NULL;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_season_nodes_parent_same_season'
    ) THEN
        ALTER TABLE season_nodes
            ADD CONSTRAINT fk_season_nodes_parent_same_season
            FOREIGN KEY (season_id, parent_node_id)
            REFERENCES season_nodes (season_id, id)
            ON DELETE CASCADE;
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'chk_season_nodes_root_parent_shape'
    ) THEN
        ALTER TABLE season_nodes
            ADD CONSTRAINT chk_season_nodes_root_parent_shape
            CHECK (
                (node_type = 'root' AND parent_node_id IS NULL)
                OR (node_type <> 'root' AND parent_node_id IS NOT NULL)
            );
    END IF;
END $$;

ALTER TABLE season_nodes ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'season_nodes' AND policyname = 'season_nodes_public_select'
    ) THEN
        CREATE POLICY season_nodes_public_select ON season_nodes
            FOR SELECT TO authenticated
            USING (
                EXISTS (
                    SELECT 1
                    FROM seasons s
                    WHERE s.id = season_nodes.season_id
                      AND s.is_public = TRUE
                )
            );
    END IF;
END $$;

-- ── Points + qualification rules ─────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS season_points_rules (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id                   UUID NOT NULL REFERENCES seasons(id) ON DELETE CASCADE,
    source_node_id              UUID NOT NULL REFERENCES season_nodes(id) ON DELETE CASCADE,
    source_stage_id             UUID NULL REFERENCES tournament_stages(id) ON DELETE CASCADE,
    destination_node_id         UUID NULL REFERENCES season_nodes(id) ON DELETE SET NULL,
    placement_from              INT NOT NULL CHECK (placement_from >= 1),
    placement_to                INT NOT NULL CHECK (placement_to >= placement_from),
    points_awarded              INT NOT NULL DEFAULT 0 CHECK (points_awarded >= 0),
    qualification_status        TEXT NULL
                                    CHECK (qualification_status IS NULL OR qualification_status IN ('qualified', 'wildcard', 'reserve')),
    auto_create_qualification   BOOLEAN NOT NULL DEFAULT FALSE,
    region_key                  TEXT NULL,
    created_at                  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at                  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_season_points_rules_source
    ON season_points_rules (season_id, source_node_id, placement_from, placement_to);

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_season_points_rules_source_node_same_season'
    ) THEN
        ALTER TABLE season_points_rules
            ADD CONSTRAINT fk_season_points_rules_source_node_same_season
            FOREIGN KEY (season_id, source_node_id)
            REFERENCES season_nodes (season_id, id)
            ON DELETE CASCADE;
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_season_points_rules_destination_node_same_season'
    ) THEN
        ALTER TABLE season_points_rules
            ADD CONSTRAINT fk_season_points_rules_destination_node_same_season
            FOREIGN KEY (season_id, destination_node_id)
            REFERENCES season_nodes (season_id, id)
            ON DELETE SET NULL;
    END IF;
END $$;

ALTER TABLE season_points_rules ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'season_points_rules' AND policyname = 'season_points_rules_public_select'
    ) THEN
        CREATE POLICY season_points_rules_public_select ON season_points_rules
            FOR SELECT TO authenticated
            USING (
                EXISTS (
                    SELECT 1
                    FROM seasons s
                    WHERE s.id = season_points_rules.season_id
                      AND s.is_public = TRUE
                )
            );
    END IF;
END $$;

-- ── Branch locks: one sibling qualifier choice per participant entry ────────
CREATE TABLE IF NOT EXISTS season_entry_locks (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id                   UUID NOT NULL REFERENCES seasons(id) ON DELETE CASCADE,
    season_node_id              UUID NOT NULL REFERENCES season_nodes(id) ON DELETE CASCADE,
    lock_group_node_id          UUID NULL REFERENCES season_nodes(id) ON DELETE CASCADE,
    linked_tournament_id        UUID NULL REFERENCES tournaments(id) ON DELETE SET NULL,
    tournament_participant_id   UUID NULL REFERENCES tournament_participants(id) ON DELETE SET NULL,
    participant_mode            TEXT NOT NULL CHECK (participant_mode IN ('team', 'solo')),
    team_id                     UUID NULL REFERENCES teams(id) ON DELETE CASCADE,
    user_id                     UUID NULL REFERENCES profiles(id) ON DELETE CASCADE,
    status                      TEXT NOT NULL DEFAULT 'locked'
                                    CHECK (status IN ('locked', 'reassigned', 'revoked')),
    locked_by                   TEXT NOT NULL DEFAULT 'registration'
                                    CHECK (locked_by IN ('registration', 'qualification', 'admin_override')),
    notes                       TEXT NULL,
    created_at                  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at                  TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK ((team_id IS NULL) <> (user_id IS NULL))
);

CREATE INDEX IF NOT EXISTS idx_season_entry_locks_season_group
    ON season_entry_locks (season_id, lock_group_node_id, status);

CREATE UNIQUE INDEX IF NOT EXISTS uq_season_entry_locks_team_group
    ON season_entry_locks (season_id, lock_group_node_id, team_id)
    WHERE team_id IS NOT NULL AND status = 'locked';

CREATE UNIQUE INDEX IF NOT EXISTS uq_season_entry_locks_user_group
    ON season_entry_locks (season_id, lock_group_node_id, user_id)
    WHERE user_id IS NOT NULL AND status = 'locked';

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_season_entry_locks_node_same_season'
    ) THEN
        ALTER TABLE season_entry_locks
            ADD CONSTRAINT fk_season_entry_locks_node_same_season
            FOREIGN KEY (season_id, season_node_id)
            REFERENCES season_nodes (season_id, id)
            ON DELETE CASCADE;
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_season_entry_locks_group_same_season'
    ) THEN
        ALTER TABLE season_entry_locks
            ADD CONSTRAINT fk_season_entry_locks_group_same_season
            FOREIGN KEY (season_id, lock_group_node_id)
            REFERENCES season_nodes (season_id, id)
            ON DELETE CASCADE;
    END IF;
END $$;

ALTER TABLE season_entry_locks ENABLE ROW LEVEL SECURITY;

-- ── Qualification records ────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS season_qualification_records (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id               UUID NOT NULL REFERENCES seasons(id) ON DELETE CASCADE,
    source_node_id          UUID NOT NULL REFERENCES season_nodes(id) ON DELETE CASCADE,
    source_stage_id         UUID NULL REFERENCES tournament_stages(id) ON DELETE SET NULL,
    destination_node_id     UUID NULL REFERENCES season_nodes(id) ON DELETE SET NULL,
    source_tournament_id    UUID NULL REFERENCES tournaments(id) ON DELETE SET NULL,
    participant_mode        TEXT NOT NULL CHECK (participant_mode IN ('team', 'solo')),
    team_id                 UUID NULL REFERENCES teams(id) ON DELETE CASCADE,
    user_id                 UUID NULL REFERENCES profiles(id) ON DELETE CASCADE,
    placement               INT NULL CHECK (placement IS NULL OR placement >= 1),
    points_snapshot         INT NULL,
    status                  TEXT NOT NULL DEFAULT 'earned'
                                CHECK (status IN ('earned', 'confirmed', 'invited', 'accepted', 'declined', 'revoked', 'overridden')),
    is_manual_override      BOOLEAN NOT NULL DEFAULT FALSE,
    notes                   TEXT NULL,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK ((team_id IS NULL) <> (user_id IS NULL))
);

CREATE INDEX IF NOT EXISTS idx_season_qualification_records_season_status
    ON season_qualification_records (season_id, status, created_at DESC);

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_season_qualification_source_node_same_season'
    ) THEN
        ALTER TABLE season_qualification_records
            ADD CONSTRAINT fk_season_qualification_source_node_same_season
            FOREIGN KEY (season_id, source_node_id)
            REFERENCES season_nodes (season_id, id)
            ON DELETE CASCADE;
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_season_qualification_destination_node_same_season'
    ) THEN
        ALTER TABLE season_qualification_records
            ADD CONSTRAINT fk_season_qualification_destination_node_same_season
            FOREIGN KEY (season_id, destination_node_id)
            REFERENCES season_nodes (season_id, id)
            ON DELETE SET NULL;
    END IF;
END $$;

ALTER TABLE season_qualification_records ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'season_qualification_records'
          AND policyname = 'season_qualification_records_public_select'
    ) THEN
        CREATE POLICY season_qualification_records_public_select ON season_qualification_records
            FOR SELECT TO authenticated
            USING (
                EXISTS (
                    SELECT 1
                    FROM seasons s
                    WHERE s.id = season_qualification_records.season_id
                      AND s.is_public = TRUE
                )
            );
    END IF;
END $$;

-- ── Points ledger ────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS season_points_ledger (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id               UUID NOT NULL REFERENCES seasons(id) ON DELETE CASCADE,
    source_node_id          UUID NOT NULL REFERENCES season_nodes(id) ON DELETE CASCADE,
    source_stage_id         UUID NULL REFERENCES tournament_stages(id) ON DELETE SET NULL,
    source_tournament_id    UUID NULL REFERENCES tournaments(id) ON DELETE SET NULL,
    participant_mode        TEXT NOT NULL CHECK (participant_mode IN ('team', 'solo')),
    team_id                 UUID NULL REFERENCES teams(id) ON DELETE CASCADE,
    user_id                 UUID NULL REFERENCES profiles(id) ON DELETE CASCADE,
    region_key              TEXT NULL,
    points                  INT NOT NULL,
    placement               INT NULL CHECK (placement IS NULL OR placement >= 1),
    reason                  TEXT NOT NULL,
    is_manual_override      BOOLEAN NOT NULL DEFAULT FALSE,
    notes                   TEXT NULL,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK ((team_id IS NULL) <> (user_id IS NULL))
);

CREATE INDEX IF NOT EXISTS idx_season_points_ledger_season_region
    ON season_points_ledger (season_id, region_key, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_season_points_ledger_team
    ON season_points_ledger (season_id, team_id)
    WHERE team_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_season_points_ledger_user
    ON season_points_ledger (season_id, user_id)
    WHERE user_id IS NOT NULL;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_season_points_ledger_source_node_same_season'
    ) THEN
        ALTER TABLE season_points_ledger
            ADD CONSTRAINT fk_season_points_ledger_source_node_same_season
            FOREIGN KEY (season_id, source_node_id)
            REFERENCES season_nodes (season_id, id)
            ON DELETE CASCADE;
    END IF;
END $$;

ALTER TABLE season_points_ledger ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'season_points_ledger'
          AND policyname = 'season_points_ledger_public_select'
    ) THEN
        CREATE POLICY season_points_ledger_public_select ON season_points_ledger
            FOR SELECT TO authenticated
            USING (
                EXISTS (
                    SELECT 1
                    FROM seasons s
                    WHERE s.id = season_points_ledger.season_id
                      AND s.is_public = TRUE
                )
            );
    END IF;
END $$;

-- ── Grants ───────────────────────────────────────────────────────────────────
GRANT ALL ON seasons TO service_role;
GRANT ALL ON season_staff TO service_role;
GRANT ALL ON season_nodes TO service_role;
GRANT ALL ON season_points_rules TO service_role;
GRANT ALL ON season_entry_locks TO service_role;
GRANT ALL ON season_qualification_records TO service_role;
GRANT ALL ON season_points_ledger TO service_role;
GRANT ALL ON profile_private_details TO service_role;

GRANT SELECT ON seasons TO authenticated;
GRANT SELECT ON season_nodes TO authenticated;
GRANT SELECT ON season_points_rules TO authenticated;
GRANT SELECT ON season_qualification_records TO authenticated;
GRANT SELECT ON season_points_ledger TO authenticated;
GRANT SELECT, INSERT, UPDATE ON profile_private_details TO authenticated;
