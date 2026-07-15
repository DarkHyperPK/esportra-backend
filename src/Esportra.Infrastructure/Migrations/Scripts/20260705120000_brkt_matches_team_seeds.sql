-- Add team seed columns to brkt_matches for persistent seed display across rounds
ALTER TABLE brkt_matches ADD COLUMN IF NOT EXISTS team1_seed INT;
ALTER TABLE brkt_matches ADD COLUMN IF NOT EXISTS team2_seed INT;
