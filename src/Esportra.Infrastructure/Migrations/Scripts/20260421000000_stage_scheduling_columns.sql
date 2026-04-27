-- Add scheduling columns to tournament_stages
ALTER TABLE tournament_stages ADD COLUMN IF NOT EXISTS starts_at TIMESTAMPTZ;
ALTER TABLE tournament_stages ADD COLUMN IF NOT EXISTS ends_at TIMESTAMPTZ;
