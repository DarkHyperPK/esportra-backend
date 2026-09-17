-- Backfill tournament_participants.team_name with the authoritative teams.name.
-- Rows written before this fix stored the client-supplied team name at registration
-- time, which diverged when teams were renamed after registering.
--
-- PRE-FLIGHT: Run on production before deploying, confirm expected count before proceeding:
-- SELECT COUNT(*)
--   FROM tournament_participants tp
--   JOIN teams t ON t.id = tp.team_id
--  WHERE tp.team_name IS DISTINCT FROM t.name;
-- Expected: small number of rows whose team_name predates a rename

UPDATE tournament_participants tp
SET    team_name = t.name
FROM   teams t
WHERE  tp.team_id = t.id
  AND  tp.team_name IS DISTINCT FROM t.name;
