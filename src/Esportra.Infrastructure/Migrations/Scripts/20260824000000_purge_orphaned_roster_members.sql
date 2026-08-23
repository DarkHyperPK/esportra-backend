-- Purge roster membership rows for users who are no longer active members of the
-- owning team. Historical leave/remove paths deleted from team_members without
-- cleaning team_roster_members, leaving ghost players rendered in lineups.
-- Idempotent: only deletes rows that are currently orphaned.

DELETE FROM public.team_roster_members trm
USING public.team_rosters tr
WHERE trm.roster_id = tr.id
  AND NOT EXISTS (
      SELECT 1 FROM public.team_members tm
      WHERE tm.team_id = tr.team_id
        AND tm.user_id = trm.user_id
        AND tm.is_active = TRUE);
