-- Staging repair: ensure relational BR evidence table, columns, grants, and service_role RLS.
-- Safe to re-run on production after 20260521140000_br_round_evidence.sql.

CREATE TABLE IF NOT EXISTS br_round_evidence (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    round_id         UUID        NOT NULL REFERENCES br_rounds(id) ON DELETE CASCADE,
    team_id          UUID        NULL REFERENCES teams(id) ON DELETE CASCADE,
    participant_id   UUID        NULL REFERENCES tournament_participants(id) ON DELETE CASCADE,
    image_url        TEXT        NOT NULL,
    submitted_by     UUID        NOT NULL,
    submitted_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    placement        INT         NULL CHECK (placement IS NULL OR placement >= 1),
    kills            INT         NULL CHECK (kills IS NULL OR kills >= 0),
    reviewed         BOOLEAN     NOT NULL DEFAULT FALSE,
    reviewed_at      TIMESTAMPTZ NULL,
    reviewed_by      UUID        NULL,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT br_round_evidence_exactly_one_entity
        CHECK ((team_id IS NULL) != (participant_id IS NULL))
);

ALTER TABLE br_round_evidence ADD COLUMN IF NOT EXISTS participant_id UUID NULL REFERENCES tournament_participants(id) ON DELETE CASCADE;
ALTER TABLE br_round_evidence ADD COLUMN IF NOT EXISTS placement INT NULL;
ALTER TABLE br_round_evidence ADD COLUMN IF NOT EXISTS kills INT NULL;
ALTER TABLE br_round_evidence ADD COLUMN IF NOT EXISTS reviewed BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE br_round_evidence ADD COLUMN IF NOT EXISTS reviewed_at TIMESTAMPTZ NULL;
ALTER TABLE br_round_evidence ADD COLUMN IF NOT EXISTS reviewed_by UUID NULL;
ALTER TABLE br_round_evidence ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT now();
ALTER TABLE br_round_evidence ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT now();

CREATE INDEX IF NOT EXISTS idx_br_round_evidence_round_id
    ON br_round_evidence (round_id);

CREATE INDEX IF NOT EXISTS idx_br_round_evidence_participant_id
    ON br_round_evidence (participant_id);

CREATE UNIQUE INDEX IF NOT EXISTS uq_br_round_evidence_team
    ON br_round_evidence (round_id, team_id)
    WHERE team_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_br_round_evidence_participant
    ON br_round_evidence (round_id, participant_id)
    WHERE participant_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_br_round_evidence_pending_review
    ON br_round_evidence (round_id)
    WHERE reviewed = FALSE;

ALTER TABLE br_round_evidence ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_round_evidence' AND policyname = 'br_round_evidence_authenticated_select'
    ) THEN
        CREATE POLICY br_round_evidence_authenticated_select ON br_round_evidence
            FOR SELECT TO authenticated USING (true);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_round_evidence' AND policyname = 'br_round_evidence_authenticated_insert'
    ) THEN
        CREATE POLICY br_round_evidence_authenticated_insert ON br_round_evidence
            FOR INSERT TO authenticated WITH CHECK (true);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_round_evidence' AND policyname = 'br_round_evidence_authenticated_update'
    ) THEN
        CREATE POLICY br_round_evidence_authenticated_update ON br_round_evidence
            FOR UPDATE TO authenticated USING (true) WITH CHECK (true);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'br_round_evidence' AND policyname = 'br_round_evidence_authenticated_delete'
    ) THEN
        CREATE POLICY br_round_evidence_authenticated_delete ON br_round_evidence
            FOR DELETE TO authenticated USING (true);
    END IF;
END $$;

DROP POLICY IF EXISTS br_round_evidence_service_role_all ON br_round_evidence;
CREATE POLICY br_round_evidence_service_role_all ON br_round_evidence
    FOR ALL TO service_role USING (true) WITH CHECK (true);

CREATE OR REPLACE FUNCTION update_br_round_evidence_updated_at()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at := now();
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_br_round_evidence_updated_at ON br_round_evidence;

CREATE TRIGGER trg_br_round_evidence_updated_at
    BEFORE UPDATE ON br_round_evidence
    FOR EACH ROW
    EXECUTE FUNCTION update_br_round_evidence_updated_at();

GRANT ALL ON br_round_evidence TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON br_round_evidence TO authenticated;
