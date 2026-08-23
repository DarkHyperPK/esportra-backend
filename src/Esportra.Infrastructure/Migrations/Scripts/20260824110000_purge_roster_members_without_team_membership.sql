-- Domain invariant: a player cannot be on a roster without an active membership
-- in the owning team. Purge lineup rows that violate it (historical leave/remove
-- paths deleted from team_members without cleaning team_roster_members).
-- Idempotent: only deletes rows with no active team_members row for the team.

DELETE FROM public.team_roster_members trm
USING public.team_rosters tr
WHERE trm.roster_id = tr.id
  AND NOT EXISTS (
      SELECT 1 FROM public.team_members tm
      WHERE tm.team_id = tr.team_id
        AND tm.user_id = trm.user_id
        AND tm.is_active = TRUE);
