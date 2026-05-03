-- ============================================================================
-- Migration: Season Inline Tournament Configuration + Advancement Graph
-- Purpose:   Promote per-node tournament configuration and per-node advancement
--            connections from ad-hoc `season_nodes.metadata` JSON into
--            first-class columns and a dedicated connections table.
--
-- Rationale: Storing tournament configuration and the advancement graph in
--            a free-form JSONB blob made it impossible to:
--              * Enforce integrity (uniqueness, cycle-freedom, same-season FKs).
--              * Index or query by config fields (format, registration type).
--              * Safely publish a season as a batch of real tournaments.
--
--            This migration:
--              1. Adds strongly-typed columns to `season_nodes` for
--                 format/team size/registration/etc.
--              2. Creates `season_advancement_connections` with proper
--                 same-season FKs, check constraints, and RLS.
--              3. Backfills both from any existing `metadata` payloads so
--                 the rollout is seamless for in-flight seasons.
--              4. Leaves the legacy `metadata` keys in place (to be cleaned
--                 up by a follow-up migration once the rollout is verified).
-- ============================================================================

-- ── 1. season_nodes: inline tournament configuration columns ─────────────────
ALTER TABLE season_nodes
    ADD COLUMN IF NOT EXISTS tournament_format        TEXT NULL,
    ADD COLUMN IF NOT EXISTS team_size                INT  NULL,
    ADD COLUMN IF NOT EXISTS max_teams                INT  NULL,
    ADD COLUMN IF NOT EXISTS min_teams                INT  NULL,
    ADD COLUMN IF NOT EXISTS best_of                  INT  NULL,
    ADD COLUMN IF NOT EXISTS registration_type        TEXT NULL,
    ADD COLUMN IF NOT EXISTS entry_fee                NUMERIC(12,2) NULL,
    ADD COLUMN IF NOT EXISTS prize_pool               NUMERIC(14,2) NULL,
    ADD COLUMN IF NOT EXISTS check_in_minutes_before  INT  NULL,
    ADD COLUMN IF NOT EXISTS registration_opens_at    TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS published_tournament_id  UUID NULL
        REFERENCES tournaments(id) ON DELETE SET NULL;

-- Deferred check constraints (added separately for IF NOT EXISTS semantics)
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_season_nodes_tournament_format') THEN
        ALTER TABLE season_nodes
            ADD CONSTRAINT chk_season_nodes_tournament_format
            CHECK (tournament_format IS NULL OR tournament_format IN (
                'single_elimination',
                'double_elimination',
                'round_robin',
                'swiss',
                'groups_playoffs',
                'battle_royale'
            ));
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_season_nodes_team_size') THEN
        ALTER TABLE season_nodes
            ADD CONSTRAINT chk_season_nodes_team_size
            CHECK (team_size IS NULL OR (team_size >= 1 AND team_size <= 20));
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_season_nodes_max_teams') THEN
        ALTER TABLE season_nodes
            ADD CONSTRAINT chk_season_nodes_max_teams
            CHECK (max_teams IS NULL OR (max_teams >= 2 AND max_teams <= 4096));
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_season_nodes_min_teams') THEN
        ALTER TABLE season_nodes
            ADD CONSTRAINT chk_season_nodes_min_teams
            CHECK (min_teams IS NULL OR min_teams >= 2);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_season_nodes_best_of') THEN
        ALTER TABLE season_nodes
            ADD CONSTRAINT chk_season_nodes_best_of
            CHECK (best_of IS NULL OR (best_of >= 1 AND best_of <= 9));
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_season_nodes_registration_type') THEN
        ALTER TABLE season_nodes
            ADD CONSTRAINT chk_season_nodes_registration_type
            CHECK (registration_type IS NULL OR registration_type IN ('open', 'invite', 'qualifier_feed'));
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_season_nodes_entry_fee') THEN
        ALTER TABLE season_nodes
            ADD CONSTRAINT chk_season_nodes_entry_fee
            CHECK (entry_fee IS NULL OR entry_fee >= 0);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_season_nodes_prize_pool') THEN
        ALTER TABLE season_nodes
            ADD CONSTRAINT chk_season_nodes_prize_pool
            CHECK (prize_pool IS NULL OR prize_pool >= 0);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_season_nodes_check_in_minutes_before') THEN
        ALTER TABLE season_nodes
            ADD CONSTRAINT chk_season_nodes_check_in_minutes_before
            CHECK (check_in_minutes_before IS NULL OR check_in_minutes_before >= 0);
    END IF;
END $$;

-- Ensure min_teams ≤ max_teams when both supplied
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_season_nodes_team_counts_ordering') THEN
        ALTER TABLE season_nodes
            ADD CONSTRAINT chk_season_nodes_team_counts_ordering
            CHECK (min_teams IS NULL OR max_teams IS NULL OR min_teams <= max_teams);
    END IF;
END $$;

-- Fast lookup of all nodes a tournament was published under
CREATE UNIQUE INDEX IF NOT EXISTS uq_season_nodes_published_tournament
    ON season_nodes (published_tournament_id)
    WHERE published_tournament_id IS NOT NULL;

-- ── 2. season_advancement_connections ────────────────────────────────────────
CREATE TABLE IF NOT EXISTS season_advancement_connections (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id       UUID NOT NULL REFERENCES seasons(id) ON DELETE CASCADE,
    from_node_id    UUID NOT NULL REFERENCES season_nodes(id) ON DELETE CASCADE,
    to_node_id      UUID NOT NULL REFERENCES season_nodes(id) ON DELETE CASCADE,
    rule_type       TEXT NOT NULL
                        CHECK (rule_type IN ('top_n', 'top_percentage', 'points_threshold', 'manual_selection')),
    rule_value      NUMERIC(10,2) NOT NULL CHECK (rule_value > 0),
    seed_mode       TEXT NOT NULL DEFAULT 'preserve_seed'
                        CHECK (seed_mode IN ('preserve_seed', 'reseed_by_points', 'randomize', 'manual')),
    label           TEXT NULL,
    display_order   INT  NOT NULL DEFAULT 0,
    metadata        JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (from_node_id <> to_node_id)
);

CREATE INDEX IF NOT EXISTS idx_sac_season_from
    ON season_advancement_connections (season_id, from_node_id, display_order);

CREATE INDEX IF NOT EXISTS idx_sac_season_to
    ON season_advancement_connections (season_id, to_node_id);

-- Enforce that from/to nodes belong to the same season as the connection row
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_sac_from_same_season') THEN
        ALTER TABLE season_advancement_connections
            ADD CONSTRAINT fk_sac_from_same_season
            FOREIGN KEY (season_id, from_node_id)
            REFERENCES season_nodes (season_id, id)
            ON DELETE CASCADE;
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_sac_to_same_season') THEN
        ALTER TABLE season_advancement_connections
            ADD CONSTRAINT fk_sac_to_same_season
            FOREIGN KEY (season_id, to_node_id)
            REFERENCES season_nodes (season_id, id)
            ON DELETE CASCADE;
    END IF;
END $$;

-- Prevent accidental duplicate edges between the same two nodes.
-- Multiple distinct connections between the same (from, to) pair are still
-- possible via different labels if needed; allow that by keying the uniqueness
-- on (from_node_id, to_node_id, label) where label is NULL-unique via COALESCE.
CREATE UNIQUE INDEX IF NOT EXISTS uq_sac_from_to_label
    ON season_advancement_connections (from_node_id, to_node_id, COALESCE(label, ''));

-- ── 3. RLS + grants ──────────────────────────────────────────────────────────
ALTER TABLE season_advancement_connections ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'season_advancement_connections'
          AND policyname = 'season_advancement_connections_public_select'
    ) THEN
        CREATE POLICY season_advancement_connections_public_select ON season_advancement_connections
            FOR SELECT TO authenticated
            USING (
                EXISTS (
                    SELECT 1
                    FROM seasons s
                    WHERE s.id = season_advancement_connections.season_id
                      AND s.is_public = TRUE
                )
            );
    END IF;
END $$;

GRANT ALL ON season_advancement_connections TO service_role;
GRANT SELECT ON season_advancement_connections TO authenticated;

-- ── 4. Backfill: copy tournamentConfig from metadata into first-class columns ─
UPDATE season_nodes sn
SET tournament_format       = COALESCE(sn.tournament_format,
                                       NULLIF(sn.metadata->'tournamentConfig'->>'format', '')),
    team_size               = COALESCE(sn.team_size,
                                       NULLIF(sn.metadata->'tournamentConfig'->>'teamSize', '')::int),
    max_teams               = COALESCE(sn.max_teams,
                                       NULLIF(sn.metadata->'tournamentConfig'->>'maxTeams', '')::int),
    min_teams               = COALESCE(sn.min_teams,
                                       NULLIF(sn.metadata->'tournamentConfig'->>'minTeams', '')::int),
    best_of                 = COALESCE(sn.best_of,
                                       NULLIF(sn.metadata->'tournamentConfig'->>'bestOf', '')::int),
    registration_type       = COALESCE(sn.registration_type,
                                       NULLIF(sn.metadata->'tournamentConfig'->>'registrationType', '')),
    entry_fee               = COALESCE(sn.entry_fee,
                                       NULLIF(sn.metadata->'tournamentConfig'->>'entryFee', '')::numeric),
    prize_pool              = COALESCE(sn.prize_pool,
                                       NULLIF(sn.metadata->'tournamentConfig'->>'prizePool', '')::numeric),
    check_in_minutes_before = COALESCE(sn.check_in_minutes_before,
                                       NULLIF(sn.metadata->'tournamentConfig'->>'checkInMinutesBefore', '')::int),
    registration_opens_at   = COALESCE(sn.registration_opens_at,
                                       NULLIF(sn.metadata->'tournamentConfig'->>'registrationOpensAt', '')::timestamptz),
    updated_at              = NOW()
WHERE sn.metadata ? 'tournamentConfig'
  AND jsonb_typeof(sn.metadata->'tournamentConfig') = 'object';

-- ── 5. Backfill: advancement connections from metadata.advancement.outgoing ──
-- Only emit rows where:
--   * outgoing is an array
--   * the referenced target node belongs to the same season
--   * (from, to, label) tuple is not already present
INSERT INTO season_advancement_connections (
    id, season_id, from_node_id, to_node_id,
    rule_type, rule_value, seed_mode, label, display_order
)
SELECT
    COALESCE(
        CASE WHEN jsonb_typeof(conn->'id') = 'string'
             THEN NULLIF(conn->>'id', '')::uuid
             ELSE NULL
        END,
        gen_random_uuid()
    ),
    sn.season_id,
    sn.id,
    (conn->>'toNodeId')::uuid,
    COALESCE(NULLIF(conn->>'ruleType', ''), 'top_n'),
    COALESCE(NULLIF(conn->>'ruleValue', '')::numeric, 1),
    COALESCE(NULLIF(conn->>'seedMode', ''), 'preserve_seed'),
    NULLIF(conn->>'label', ''),
    COALESCE(NULLIF(conn->>'displayOrder', '')::int, (ordinality - 1)::int)
FROM season_nodes sn,
     LATERAL jsonb_array_elements(
         COALESCE(sn.metadata->'advancement'->'outgoing', '[]'::jsonb)
     ) WITH ORDINALITY AS t(conn, ordinality)
WHERE jsonb_typeof(COALESCE(sn.metadata->'advancement'->'outgoing', '[]'::jsonb)) = 'array'
  AND conn ? 'toNodeId'
  AND NULLIF(conn->>'toNodeId', '') IS NOT NULL
  AND EXISTS (
      SELECT 1
      FROM season_nodes target
      WHERE target.season_id = sn.season_id
        AND target.id = (conn->>'toNodeId')::uuid
  )
  AND COALESCE(NULLIF(conn->>'ruleType', ''), 'top_n') IN
      ('top_n', 'top_percentage', 'points_threshold', 'manual_selection')
  AND COALESCE(NULLIF(conn->>'ruleValue', '')::numeric, 1) > 0
  AND sn.id <> (conn->>'toNodeId')::uuid
ON CONFLICT (from_node_id, to_node_id, COALESCE(label, '')) DO NOTHING;

-- ============================================================================
-- Post-conditions:
--   * Every `season_nodes` row retains its original `metadata`.
--   * New columns populated where a tournamentConfig existed in metadata.
--   * Every discoverable outgoing advancement edge is now a row in
--     season_advancement_connections.
--   * A follow-up migration may strip `tournamentConfig` and `advancement`
--     from `season_nodes.metadata` once the new code has shipped and the
--     backfill has been verified in production.
-- ============================================================================
