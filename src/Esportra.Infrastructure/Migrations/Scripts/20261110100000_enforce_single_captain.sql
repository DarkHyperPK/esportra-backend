-- Backfill captain_id for teams where it is NULL (use active captain from team_members)
UPDATE teams t
SET captain_id = (
    SELECT user_id FROM team_members tm
    WHERE tm.team_id = t.id AND tm.role = 'captain' AND tm.is_active = true
    LIMIT 1
)
WHERE t.captain_id IS NULL
  AND EXISTS (
    SELECT 1 FROM team_members tm
    WHERE tm.team_id = t.id AND tm.role = 'captain' AND tm.is_active = true
  );

-- For teams still NULL (no captain row in team_members), fall back to owner_id
UPDATE teams SET captain_id = owner_id WHERE captain_id IS NULL;

-- Demote extra captains: for each team, keep only the one matching captain_id
-- (or the earliest-joined one if captain_id doesn't match any), demote the rest to 'member'.
UPDATE team_members tm
SET role = 'member'
WHERE tm.role = 'captain'
  AND tm.is_active = true
  AND tm.ctid NOT IN (
    SELECT DISTINCT ON (sub.team_id) sub.ctid
    FROM team_members sub
    JOIN teams t ON t.id = sub.team_id
    WHERE sub.role = 'captain' AND sub.is_active = true
    ORDER BY sub.team_id,
             (sub.user_id = t.captain_id) DESC,
             sub.joined_at ASC NULLS LAST
  );

-- Enforce exactly one active captain per team at the database level.
-- The transfer-captain endpoint demotes the old captain before promoting the new one
-- within a single transaction, so this constraint is safe for existing flows.
CREATE UNIQUE INDEX IF NOT EXISTS idx_team_members_one_captain_per_team
    ON team_members (team_id) WHERE role = 'captain' AND is_active = true;
