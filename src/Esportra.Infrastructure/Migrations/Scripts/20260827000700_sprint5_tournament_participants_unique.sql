-- L12: Unique constraint on tournament_participants(tournament_id, team_id)
-- Partial: exclude cancelled/rejected so historical records are preserved
--
-- Pre-flight: if a team has multiple active registrations for the same tournament
-- (possible from testing or a race-condition bug), cancel the older duplicates
-- keeping only the highest-priority one (approved > pending > any other).
WITH ranked AS (
    SELECT id,
           ROW_NUMBER() OVER (
               PARTITION BY tournament_id, team_id
               ORDER BY
                   CASE status
                       WHEN 'approved'  THEN 1
                       WHEN 'checked_in' THEN 2
                       WHEN 'pending'   THEN 3
                       ELSE 4
                   END,
                   created_at DESC
           ) AS rn
    FROM tournament_participants
    WHERE status NOT IN ('cancelled', 'rejected')
)
UPDATE tournament_participants
SET status = 'cancelled'
WHERE id IN (SELECT id FROM ranked WHERE rn > 1);

CREATE UNIQUE INDEX IF NOT EXISTS uq_tp_tournament_team
    ON tournament_participants (tournament_id, team_id)
    WHERE status NOT IN ('cancelled', 'rejected');
