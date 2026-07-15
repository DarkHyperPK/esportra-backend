-- Repair br_lobby_evidence grants/RLS after pro-lobby rename (safe to re-run).

DO $$ BEGIN
    IF to_regclass('public.br_lobby_evidence') IS NULL THEN
        RAISE NOTICE 'br_lobby_evidence missing — skipping grants repair';
        RETURN;
    END IF;

    ALTER TABLE br_lobby_evidence ENABLE ROW LEVEL SECURITY;

    DROP POLICY IF EXISTS br_lobby_evidence_service_role_all ON br_lobby_evidence;
    CREATE POLICY br_lobby_evidence_service_role_all ON br_lobby_evidence
        FOR ALL TO service_role USING (true) WITH CHECK (true);

    GRANT SELECT, INSERT, UPDATE, DELETE ON br_lobby_evidence TO service_role;
END $$;
