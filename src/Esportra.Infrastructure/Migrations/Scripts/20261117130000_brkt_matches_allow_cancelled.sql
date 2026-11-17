-- Allow 'cancelled' status on bracket matches so tournament cancellation works.
-- The existing cancellation code in TournamentEndpoints already sets this value
-- but the check constraint was missing it.

ALTER TABLE brkt_matches DROP CONSTRAINT IF EXISTS brkt_matches_status_check;
ALTER TABLE brkt_matches ADD CONSTRAINT brkt_matches_status_check
    CHECK (status IN ('pending', 'scheduled', 'in_progress', 'completed', 'disputed', 'cancelled'));
