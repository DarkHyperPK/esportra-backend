-- Fix match_checkins FK violation for solo tournaments.
-- After the solo-native migration, brkt_matches.team1_id/team2_id store
-- tournament_participants.id for solo entries -- not teams.id. The FK
-- match_checkins_team_id_fkey → public.teams(id) therefore rejects every
-- solo check-in with error 23503. Drop the constraint so team_id can hold
-- either a real teams.id (team tournaments) or a tournament_participants.id
-- (solo tournaments). The application layer already validates both cases
-- before the INSERT.
--
-- Also rebuild the RLS INSERT policy to allow solo participants (who have no
-- team_members row) to check in to matches where they are a bracket slot.

-- 1. Drop the FK constraint (DROP CONSTRAINT IF EXISTS is safe to re-run)
ALTER TABLE public.match_checkins DROP CONSTRAINT IF EXISTS match_checkins_team_id_fkey;

-- 2. Rebuild the captain/solo INSERT policy
DROP POLICY IF EXISTS checkins_captain_insert ON public.match_checkins;

CREATE POLICY checkins_captain_insert ON public.match_checkins
    FOR INSERT
    TO authenticated
    WITH CHECK (
        -- Team tournament: caller must be the team captain
        EXISTS (
            SELECT 1
            FROM public.team_members tm
            WHERE tm.team_id = match_checkins.team_id
              AND tm.user_id = auth.uid()
              AND tm.role = 'captain'
        )
        OR
        -- Solo tournament: caller must be the participant whose id is stored
        -- in team_id AND that participant must be a slot in this match
        EXISTS (
            SELECT 1
            FROM public.tournament_participants tp
            JOIN public.brkt_matches bm
              ON (bm.team1_id = tp.id OR bm.team2_id = tp.id)
            WHERE tp.id = match_checkins.team_id
              AND tp.user_id = auth.uid()
              AND bm.id = match_checkins.match_id
        )
    );
