-- ============================================================================
-- Migration: BR Games Model (persistent lobby + per-game child rows)
-- Purpose:   One br_lobbies row = one physical room/session; br_games holds
--            individual scored matches inside that lobby.
-- ============================================================================

-- ── 1. br_games ─────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS br_games (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    lobby_id        UUID        NOT NULL REFERENCES br_lobbies(id) ON DELETE CASCADE,
    game_number     INT         NOT NULL,
    map             TEXT,
    status          TEXT        NOT NULL DEFAULT 'pending'
                        CHECK (status IN ('pending', 'active', 'completed')),
    scheduled_at    TIMESTAMPTZ,
    started_at      TIMESTAMPTZ,
    completed_at    TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),

    UNIQUE (lobby_id, game_number)
);

CREATE INDEX IF NOT EXISTS idx_br_games_lobby_id
    ON br_games (lobby_id);

CREATE INDEX IF NOT EXISTS idx_br_games_lobby_status
    ON br_games (lobby_id, status);

-- ── 2. Backfill one game per existing lobby ─────────────────────────────────

INSERT INTO br_games (
    lobby_id,
    game_number,
    map,
    status,
    scheduled_at,
    started_at,
    completed_at,
    created_at
)
SELECT
    l.id,
    1,
    l.map,
    l.status,
    l.scheduled_at,
    l.started_at,
    l.completed_at,
    l.created_at
FROM br_lobbies l
WHERE NOT EXISTS (
    SELECT 1 FROM br_games g WHERE g.lobby_id = l.id AND g.game_number = 1
);

-- ── 3. Repoint br_lobby_results → game_id ───────────────────────────────────

ALTER TABLE br_lobby_results ADD COLUMN IF NOT EXISTS game_id UUID;

UPDATE br_lobby_results rr
SET game_id = g.id
FROM br_games g
WHERE g.lobby_id = rr.lobby_id
  AND g.game_number = 1
  AND rr.game_id IS NULL;

ALTER TABLE br_lobby_results ALTER COLUMN game_id SET NOT NULL;

DROP INDEX IF EXISTS uq_br_lobby_results_team;
DROP INDEX IF EXISTS uq_br_lobby_results_participant;
DROP INDEX IF EXISTS uq_br_lobby_results_lobby_placement;

ALTER TABLE br_lobby_results DROP CONSTRAINT IF EXISTS br_round_results_lobby_id_fkey;
ALTER TABLE br_lobby_results DROP CONSTRAINT IF EXISTS br_lobby_results_lobby_id_fkey;

ALTER TABLE br_lobby_results
    ADD CONSTRAINT br_lobby_results_game_id_fkey
    FOREIGN KEY (game_id) REFERENCES br_games(id) ON DELETE CASCADE;

CREATE UNIQUE INDEX IF NOT EXISTS uq_br_lobby_results_game_team
    ON br_lobby_results (game_id, team_id)
    WHERE team_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_br_lobby_results_game_participant
    ON br_lobby_results (game_id, participant_id)
    WHERE participant_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_br_lobby_results_game_placement
    ON br_lobby_results (game_id, placement);

-- Keep lobby_id for transition queries (nullable after backfill)
ALTER TABLE br_lobby_results ALTER COLUMN lobby_id DROP NOT NULL;

-- ── 4. Repoint br_lobby_evidence → game_id ─────────────────────────────────

ALTER TABLE br_lobby_evidence ADD COLUMN IF NOT EXISTS game_id UUID;

UPDATE br_lobby_evidence re
SET game_id = g.id
FROM br_games g
WHERE g.lobby_id = re.lobby_id
  AND g.game_number = 1
  AND re.game_id IS NULL;

ALTER TABLE br_lobby_evidence ALTER COLUMN game_id SET NOT NULL;

DROP INDEX IF EXISTS uq_br_lobby_evidence_team;
DROP INDEX IF EXISTS uq_br_lobby_evidence_participant;

ALTER TABLE br_lobby_evidence DROP CONSTRAINT IF EXISTS br_lobby_evidence_lobby_id_fkey;

ALTER TABLE br_lobby_evidence
    ADD CONSTRAINT br_lobby_evidence_game_id_fkey
    FOREIGN KEY (game_id) REFERENCES br_games(id) ON DELETE CASCADE;

CREATE UNIQUE INDEX IF NOT EXISTS uq_br_lobby_evidence_game_team
    ON br_lobby_evidence (game_id, team_id)
    WHERE team_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_br_lobby_evidence_game_participant
    ON br_lobby_evidence (game_id, participant_id)
    WHERE participant_id IS NOT NULL;

ALTER TABLE br_lobby_evidence ALTER COLUMN lobby_id DROP NOT NULL;

-- ── 5. RLS on br_games ──────────────────────────────────────────────────────

ALTER TABLE br_games ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_games' AND policyname = 'br_games_authenticated_select'
    ) THEN
        CREATE POLICY br_games_authenticated_select ON br_games
            FOR SELECT TO authenticated USING (true);
    END IF;
END $$;

-- ── 6. Grants ───────────────────────────────────────────────────────────────

DO $$
DECLARE
    table_name TEXT;
BEGIN
    FOREACH table_name IN ARRAY ARRAY['br_games']
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
