-- Separate qualification type (qualified / wildcard / reserve) from workflow
-- status (earned / invited / accepted / revoked ...).

ALTER TABLE season_qualification_records
    ADD COLUMN IF NOT EXISTS qualification_type TEXT NULL
        CHECK (qualification_type IS NULL OR qualification_type IN ('qualified', 'wildcard', 'reserve'));

CREATE INDEX IF NOT EXISTS idx_season_qualification_records_type
    ON season_qualification_records (season_id, qualification_type, status);
