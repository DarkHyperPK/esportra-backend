-- Lobby readiness check-in: players mark "I'm in the lobby" while lobby is live.

CREATE TABLE IF NOT EXISTS br_lobby_readiness (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    lobby_id        UUID        NOT NULL REFERENCES br_lobbies(id) ON DELETE CASCADE,
    team_id         UUID        REFERENCES teams(id) ON DELETE CASCADE,
    participant_id  UUID        REFERENCES tournament_participants(id) ON DELETE CASCADE,
    user_id         UUID        NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    game_id         UUID        REFERENCES br_games(id) ON DELETE CASCADE,
    checked_in_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT br_lobby_readiness_entity_chk CHECK (
        (team_id IS NOT NULL AND participant_id IS NULL)
        OR (team_id IS NULL AND participant_id IS NOT NULL)
    )
);

CREATE INDEX IF NOT EXISTS idx_br_lobby_readiness_lobby_id
    ON br_lobby_readiness (lobby_id);

CREATE UNIQUE INDEX IF NOT EXISTS uq_br_lobby_readiness_lobby_team
    ON br_lobby_readiness (lobby_id, team_id)
    WHERE team_id IS NOT NULL AND game_id IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_br_lobby_readiness_lobby_participant
    ON br_lobby_readiness (lobby_id, participant_id)
    WHERE participant_id IS NOT NULL AND game_id IS NULL;

GRANT SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER
    ON br_lobby_readiness TO service_role;

REVOKE ALL PRIVILEGES ON br_lobby_readiness FROM authenticated;
GRANT SELECT ON br_lobby_readiness TO authenticated;
