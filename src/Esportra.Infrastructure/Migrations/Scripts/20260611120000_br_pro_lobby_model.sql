-- ============================================================================
-- Migration: BR Pro Lobby Model
-- Purpose:   Separate seed groups from physical lobbies (waves).
--            Backfill from br_rounds; repoint results/evidence; drop br_rounds.
-- ============================================================================

-- ── 1. br_lobbies (physical match instances) ─────────────────────────────────

CREATE TABLE IF NOT EXISTS br_lobbies (
    id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    stage_id            UUID        NOT NULL REFERENCES tournament_stages(id) ON DELETE CASCADE,
    wave_number         INT         NOT NULL,
    lobby_index         INT         NOT NULL DEFAULT 0,
    lobby_code          TEXT,
    map                 TEXT,
    status              TEXT        NOT NULL DEFAULT 'pending'
                            CHECK (status IN ('pending', 'active', 'completed')),
    scheduled_at        TIMESTAMPTZ,
    started_at          TIMESTAMPTZ,
    completed_at        TIMESTAMPTZ,
    queue_timer_minutes INT,
    queue_started_at    TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),

    UNIQUE (stage_id, wave_number, lobby_index),
    CONSTRAINT br_lobbies_queue_timer_minutes_check
        CHECK (queue_timer_minutes IS NULL OR (queue_timer_minutes >= 0 AND queue_timer_minutes <= 180))
);

CREATE INDEX IF NOT EXISTS idx_br_lobbies_stage_id
    ON br_lobbies (stage_id);

CREATE INDEX IF NOT EXISTS idx_br_lobbies_stage_wave
    ON br_lobbies (stage_id, wave_number);

-- ── 2. br_lobby_groups (junction: which seed groups compose a lobby) ────────

CREATE TABLE IF NOT EXISTS br_lobby_groups (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    lobby_id    UUID NOT NULL REFERENCES br_lobbies(id) ON DELETE CASCADE,
    group_id    UUID NOT NULL REFERENCES br_groups(id) ON DELETE CASCADE,

    UNIQUE (lobby_id, group_id)
);

CREATE INDEX IF NOT EXISTS idx_br_lobby_groups_group_id
    ON br_lobby_groups (group_id);

CREATE INDEX IF NOT EXISTS idx_br_lobby_groups_lobby_id
    ON br_lobby_groups (lobby_id);

-- ── 3. Backfill lobbies from br_rounds (idempotent — preserves round UUIDs) ─

INSERT INTO br_lobbies (
    id, stage_id, wave_number, lobby_index, lobby_code, map, status,
    scheduled_at, started_at, completed_at, queue_timer_minutes, queue_started_at, created_at
)
SELECT
    r.id,
    g.stage_id,
    r.round_number,
    0,
    r.lobby_code,
    NULL::TEXT,  -- map column added in later migration (20260622110000)
    r.status,
    r.scheduled_at,
    r.started_at,
    r.completed_at,
    r.queue_timer_minutes,
    r.queue_started_at,
    r.created_at
FROM br_rounds r
JOIN br_groups g ON g.id = r.group_id
ON CONFLICT (id) DO NOTHING;

INSERT INTO br_lobby_groups (lobby_id, group_id)
SELECT r.id, r.group_id
FROM br_rounds r
ON CONFLICT (lobby_id, group_id) DO NOTHING;

-- ── 4. Repoint br_round_results → br_lobby_results ─────────────────────────

ALTER TABLE br_round_results ADD COLUMN IF NOT EXISTS lobby_id UUID;

UPDATE br_round_results
SET lobby_id = round_id
WHERE lobby_id IS NULL;

ALTER TABLE br_round_results ALTER COLUMN lobby_id SET NOT NULL;

ALTER TABLE br_round_results DROP CONSTRAINT IF EXISTS br_round_results_round_id_fkey;
ALTER TABLE br_round_results DROP CONSTRAINT IF EXISTS br_round_results_round_id_key;

DROP INDEX IF EXISTS uq_br_round_results_team;
DROP INDEX IF EXISTS uq_br_round_results_participant;
DROP INDEX IF EXISTS uq_br_round_results_round_placement;

ALTER TABLE br_round_results DROP COLUMN IF EXISTS round_id;

ALTER TABLE br_round_results
    ADD CONSTRAINT br_round_results_lobby_id_fkey
    FOREIGN KEY (lobby_id) REFERENCES br_lobbies(id) ON DELETE CASCADE;

CREATE UNIQUE INDEX IF NOT EXISTS uq_br_lobby_results_team
    ON br_round_results (lobby_id, team_id)
    WHERE team_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_br_lobby_results_participant
    ON br_round_results (lobby_id, participant_id)
    WHERE participant_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_br_lobby_results_lobby_placement
    ON br_round_results (lobby_id, placement);

ALTER TABLE br_round_results RENAME TO br_lobby_results;

-- ── 5. Repoint br_round_evidence → br_lobby_evidence ───────────────────────

ALTER TABLE br_round_evidence ADD COLUMN IF NOT EXISTS lobby_id UUID;

UPDATE br_round_evidence
SET lobby_id = round_id
WHERE lobby_id IS NULL;

ALTER TABLE br_round_evidence ALTER COLUMN lobby_id SET NOT NULL;

ALTER TABLE br_round_evidence DROP CONSTRAINT IF EXISTS br_round_evidence_round_id_fkey;

DROP INDEX IF EXISTS uq_br_round_evidence_team;
DROP INDEX IF EXISTS uq_br_round_evidence_participant;

ALTER TABLE br_round_evidence DROP COLUMN IF EXISTS round_id;

ALTER TABLE br_round_evidence
    ADD CONSTRAINT br_lobby_evidence_lobby_id_fkey
    FOREIGN KEY (lobby_id) REFERENCES br_lobbies(id) ON DELETE CASCADE;

CREATE UNIQUE INDEX IF NOT EXISTS uq_br_lobby_evidence_team
    ON br_round_evidence (lobby_id, team_id)
    WHERE team_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_br_lobby_evidence_participant
    ON br_round_evidence (lobby_id, participant_id)
    WHERE participant_id IS NOT NULL;

ALTER TABLE br_round_evidence RENAME TO br_lobby_evidence;

DROP TRIGGER IF EXISTS trg_br_round_evidence_updated_at ON br_lobby_evidence;

CREATE OR REPLACE FUNCTION update_br_lobby_evidence_updated_at()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at := now();
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_br_lobby_evidence_updated_at
    BEFORE UPDATE ON br_lobby_evidence
    FOR EACH ROW
    EXECUTE FUNCTION update_br_lobby_evidence_updated_at();

-- ── 6. Drop legacy br_rounds ────────────────────────────────────────────────

DROP TABLE IF EXISTS br_rounds CASCADE;

-- ── 7. RLS on new tables ────────────────────────────────────────────────────

ALTER TABLE br_lobbies       ENABLE ROW LEVEL SECURITY;
ALTER TABLE br_lobby_groups  ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_lobbies' AND policyname = 'br_lobbies_authenticated_select'
    ) THEN
        CREATE POLICY br_lobbies_authenticated_select ON br_lobbies
            FOR SELECT TO authenticated USING (true);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_lobby_groups' AND policyname = 'br_lobby_groups_authenticated_select'
    ) THEN
        CREATE POLICY br_lobby_groups_authenticated_select ON br_lobby_groups
            FOR SELECT TO authenticated USING (true);
    END IF;
END $$;

-- ── 8. Grants (service_role writes; authenticated read-only) ────────────────

DO $$
DECLARE
    table_name TEXT;
BEGIN
    FOREACH table_name IN ARRAY ARRAY['br_lobbies', 'br_lobby_groups', 'br_lobby_results', 'br_lobby_evidence']
    LOOP
        IF to_regclass(format('public.%I', table_name)) IS NULL THEN
            RAISE NOTICE 'Skipping missing table public.%', table_name;
            CONTINUE;
        END IF;

        EXECUTE format(
            'GRANT SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON public.%I TO service_role',
            table_name);
        EXECUTE format('REVOKE ALL PRIVILEGES ON public.%I FROM authenticated', table_name);
        EXECUTE format('GRANT SELECT ON public.%I TO authenticated', table_name);
    END LOOP;
END;
$$;
