-- Preserve participant qualification responses separately from manager notes.

ALTER TABLE season_qualification_records
    ADD COLUMN IF NOT EXISTS participant_response_note TEXT NULL,
    ADD COLUMN IF NOT EXISTS responded_at TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS responded_by_user_id UUID NULL REFERENCES profiles(id) ON DELETE SET NULL;

UPDATE season_qualification_records
SET participant_response_note = notes,
    responded_at = COALESCE(responded_at, updated_at)
WHERE status IN ('accepted', 'declined')
  AND participant_response_note IS NULL
  AND notes IS NOT NULL
  AND BTRIM(notes) <> '';

UPDATE season_qualification_records
SET responded_at = COALESCE(responded_at, updated_at)
WHERE status IN ('accepted', 'declined')
  AND responded_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_season_qualification_records_responded_by
    ON season_qualification_records (responded_by_user_id)
    WHERE responded_by_user_id IS NOT NULL;
