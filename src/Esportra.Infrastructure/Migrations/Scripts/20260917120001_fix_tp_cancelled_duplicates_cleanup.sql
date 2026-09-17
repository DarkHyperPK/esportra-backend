-- Delete cancelled rows that are duplicates of an active registration.
-- Migration 20260827000700 resolved duplicate (tournament_id, team_id) pairs
-- by cancelling the older row instead of deleting it. Those cancelled rows
-- are artefacts with no functional purpose; this removes them.
-- Child rows in br_matches, br_lobbies, br_round_evidence reference
-- tournament_participants(id) with ON DELETE CASCADE / ON DELETE SET NULL,
-- so the delete is safe.
--
-- PRE-FLIGHT: Run on production before deploying, confirm expected count before proceeding:
-- SELECT COUNT(*)
--   FROM tournament_participants tp
--  WHERE tp.status = 'cancelled'
--    AND tp.team_id IS NOT NULL
--    AND EXISTS (
--          SELECT 1 FROM tournament_participants tp2
--           WHERE tp2.tournament_id = tp.tournament_id
--             AND tp2.team_id      = tp.team_id
--             AND tp2.status NOT IN ('cancelled', 'rejected')
--        );
-- Expected: small number of cancelled duplicate rows from before uq_tp_tournament_team index

DELETE FROM tournament_participants tp
WHERE  tp.status   = 'cancelled'
  AND  tp.team_id IS NOT NULL
  AND  EXISTS (
         SELECT 1 FROM tournament_participants tp2
          WHERE tp2.tournament_id = tp.tournament_id
            AND tp2.team_id      = tp.team_id
            AND tp2.status NOT IN ('cancelled', 'rejected')
       );
