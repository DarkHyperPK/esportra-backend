-- L12: Unique constraint on tournament_participants(tournament_id, team_id)
-- Partial: exclude cancelled/rejected so historical records are preserved
CREATE UNIQUE INDEX IF NOT EXISTS uq_tp_tournament_team
    ON tournament_participants (tournament_id, team_id)
    WHERE status NOT IN ('cancelled', 'rejected');
