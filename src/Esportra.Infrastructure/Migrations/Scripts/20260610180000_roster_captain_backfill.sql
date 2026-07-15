-- Ensure team captains are explicit starter rows on every roster (counts toward starter validation).

INSERT INTO public.team_roster_members (roster_id, user_id, roster_role, is_starter)
SELECT tr.id, t.owner_id, 'starter'::public.roster_member_role, TRUE
FROM public.team_rosters tr
JOIN public.teams t ON t.id = tr.team_id
WHERE t.owner_id IS NOT NULL
  AND NOT EXISTS (
      SELECT 1
      FROM public.team_roster_members trm
      WHERE trm.roster_id = tr.id
        AND trm.user_id = t.owner_id
  )
ON CONFLICT (roster_id, user_id) DO NOTHING;
