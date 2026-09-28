-- Add SELECT RLS policy to tournament_disputes.
-- The parent table had no SELECT policy; dispute_comments (20260304000008) already does.
-- Without this, any future Supabase Realtime subscription or PostgREST path
-- on this table would be unprotected by default.
-- The backend connects as service_role and is unaffected operationally.
--
-- Note: tournament_staff is intentionally omitted here — it is a pre-baseline
-- Supabase-native table and not available in the DbUp replay environment.
-- Staff access to disputes is enforced at the application layer in all backends.

ALTER TABLE public.tournament_disputes ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS tournament_disputes_select_policy ON public.tournament_disputes;

CREATE POLICY tournament_disputes_select_policy ON public.tournament_disputes
FOR SELECT USING (
    -- The participant who raised the dispute sees their own dispute
    raised_by_user_id = auth.uid()
    OR
    -- Tournament organizer
    EXISTS (
        SELECT 1 FROM public.tournaments t
        WHERE t.id = tournament_disputes.tournament_id
          AND t.organizer_id = auth.uid()
    )
    OR
    -- Assigned reviewer
    assigned_to_user_id = auth.uid()
    OR
    -- Admin/moderator via profiles.admin_roles
    EXISTS (
        SELECT 1 FROM public.profiles p
        WHERE p.id = auth.uid()
          AND ('moderator' = ANY(p.admin_roles) OR 'ops_admin' = ANY(p.admin_roles))
    )
    OR
    -- Admin/moderator via admin_user_roles table
    EXISTS (
        SELECT 1 FROM public.admin_user_roles aur
        JOIN public.admin_roles ar ON ar.id = aur.role_id
        WHERE aur.user_id = auth.uid()
          AND lower(ar.name) = ANY(ARRAY['moderator', 'ops_admin'])
    )
    OR
    (SELECT auth.role()) = 'service_role'
);
