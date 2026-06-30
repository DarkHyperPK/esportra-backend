-- First-class roster lineup roles: starter, substitute, coach.

DO $$
BEGIN
    CREATE TYPE public.roster_member_role AS ENUM ('starter', 'substitute', 'coach');
EXCEPTION
    WHEN duplicate_object THEN NULL;
END $$;

ALTER TABLE public.team_roster_members
    ADD COLUMN IF NOT EXISTS is_starter BOOLEAN NOT NULL DEFAULT true;

ALTER TABLE public.team_roster_members
    ADD COLUMN IF NOT EXISTS roster_role public.roster_member_role NOT NULL DEFAULT 'starter';

ALTER TABLE public.team_roster_members
    ADD COLUMN IF NOT EXISTS display_order INT NOT NULL DEFAULT 0;

UPDATE public.team_roster_members
SET roster_role = 'substitute'::public.roster_member_role
WHERE COALESCE(is_starter, true) = false
  AND roster_role = 'starter'::public.roster_member_role;

UPDATE public.team_roster_members trm
SET roster_role = 'coach'::public.roster_member_role,
    is_starter = false
FROM public.team_rosters tr
INNER JOIN public.team_members tm
    ON tm.team_id = tr.team_id
   AND tm.is_active = true
   AND tm.role = 'coach'
WHERE tr.id = trm.roster_id
  AND tm.user_id = trm.user_id;

UPDATE public.team_roster_members
SET is_starter = (roster_role = 'starter'::public.roster_member_role);

CREATE UNIQUE INDEX IF NOT EXISTS uidx_team_roster_members_roster_user
    ON public.team_roster_members (roster_id, user_id);

ALTER TABLE public.tournament_participants
    ADD COLUMN IF NOT EXISTS roster_lineup JSONB;

GRANT SELECT, INSERT, UPDATE, DELETE ON public.team_roster_members TO service_role;
