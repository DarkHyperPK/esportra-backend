-- Fix team_invitations RLS policies to allow captains/owners to send and view invites.
-- Previously, INSERT and SELECT were restricted to team owner only, blocking captains
-- from sending roster invites. The DELETE policy already allows captains — this brings
-- INSERT and SELECT into alignment.

-- Fix INSERT: allow team owner OR team member with captain/owner role
DROP POLICY IF EXISTS team_invitations_insert_policy ON public.team_invitations;
CREATE POLICY team_invitations_insert_policy ON public.team_invitations
  FOR INSERT WITH CHECK (
    (
      EXISTS (
        SELECT 1 FROM public.teams t
        WHERE t.id = team_invitations.team_id
          AND t.owner_id = ( SELECT auth.uid() AS uid)
      )
    ) OR (
      EXISTS (
        SELECT 1 FROM public.team_members tm
        WHERE tm.team_id = team_invitations.team_id
          AND tm.user_id = ( SELECT auth.uid() AS uid)
          AND (tm.role = 'captain'::public.team_member_role OR tm.role = 'owner'::public.team_member_role)
      )
    ) OR (
      ( SELECT auth.role() AS role) = 'service_role'::text
    )
  );

-- Fix SELECT: allow invited user, team owner, team captain/owner, or service_role
DROP POLICY IF EXISTS team_invitations_select_policy ON public.team_invitations;
CREATE POLICY team_invitations_select_policy ON public.team_invitations
  FOR SELECT USING (
    (
      invited_user_id = ( SELECT auth.uid() AS uid)
    ) OR (
      EXISTS (
        SELECT 1 FROM public.teams t
        WHERE t.id = team_invitations.team_id
          AND t.owner_id = ( SELECT auth.uid() AS uid)
      )
    ) OR (
      EXISTS (
        SELECT 1 FROM public.team_members tm
        WHERE tm.team_id = team_invitations.team_id
          AND tm.user_id = ( SELECT auth.uid() AS uid)
          AND (tm.role = 'captain'::public.team_member_role OR tm.role = 'owner'::public.team_member_role)
      )
    ) OR (
      ( SELECT auth.role() AS role) = 'service_role'::text
    )
  );
