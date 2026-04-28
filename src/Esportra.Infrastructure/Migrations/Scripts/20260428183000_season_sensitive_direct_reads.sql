-- Public season qualification and ledger reads now flow through the API, which
-- returns a safe projection. Revoke direct authenticated table reads so raw
-- internal workflow fields cannot bypass those projections.

DO $$ BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename = 'season_qualification_records'
          AND policyname = 'season_qualification_records_public_select'
    ) THEN
        DROP POLICY season_qualification_records_public_select ON season_qualification_records;
    END IF;
END $$;

DO $$ BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename = 'season_points_ledger'
          AND policyname = 'season_points_ledger_public_select'
    ) THEN
        DROP POLICY season_points_ledger_public_select ON season_points_ledger;
    END IF;
END $$;

REVOKE SELECT ON season_qualification_records FROM authenticated;
REVOKE SELECT ON season_points_ledger FROM authenticated;
