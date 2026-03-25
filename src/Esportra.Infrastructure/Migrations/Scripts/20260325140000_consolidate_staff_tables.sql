-- Consolidate staff tables: remove tournament_staff, use organization_staff + staff_tournament_assignments only
-- This migration updates RLS policies that referenced tournament_staff and then drops the table.

-- ── Update dispute_comments INSERT policy ──────────────────────────────────
DROP POLICY IF EXISTS dc_insert_own_or_organizer ON public.dispute_comments;

CREATE POLICY dc_insert_own_or_organizer ON public.dispute_comments
FOR INSERT WITH CHECK (
    user_id = auth.uid()
    AND (
        -- Person who raised the dispute
        EXISTS (
            SELECT 1 FROM public.tournament_disputes td
            WHERE td.id = dispute_comments.dispute_id
              AND td.raised_by_user_id = auth.uid()
        )
        OR
        -- Any active member of either team in the disputed match
        EXISTS (
            SELECT 1 FROM public.tournament_disputes td
            JOIN public.brkt_matches bm ON bm.id = td.match_id
            JOIN public.team_members tm ON tm.team_id IN (bm.team1_id, bm.team2_id)
            WHERE td.id = dispute_comments.dispute_id
              AND tm.user_id = auth.uid()
              AND tm.is_active = true
        )
        OR
        -- Tournament organizer or assigned reviewer
        EXISTS (
            SELECT 1 FROM public.tournament_disputes td
            JOIN public.tournaments t ON t.id = td.tournament_id
            WHERE td.id = dispute_comments.dispute_id
              AND (t.organizer_id = auth.uid() OR td.assigned_to_user_id = auth.uid())
        )
        OR
        -- Organization staff with disputes:assist permission assigned to this tournament
        EXISTS (
            SELECT 1 FROM public.tournament_disputes td
            JOIN public.organization_staff os
              ON os.organization_id = (SELECT organization_id FROM tournaments WHERE id = td.tournament_id)
            JOIN public.staff_tournament_assignments sta
              ON sta.organization_staff_id = os.id AND sta.tournament_id = td.tournament_id
            WHERE td.id = dispute_comments.dispute_id
              AND os.user_id = auth.uid()
              AND os.status = 'active'
              AND 'disputes:assist' = ANY(os.permissions)
        )
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
    )
);

-- ── Update dispute_comments SELECT policy ──────────────────────────────────
DROP POLICY IF EXISTS dc_select_own_or_dispute ON public.dispute_comments;

CREATE POLICY dc_select_own_or_dispute ON public.dispute_comments
FOR SELECT USING (
    user_id = auth.uid()
    OR
    (
        NOT is_internal
        AND EXISTS (
            SELECT 1 FROM public.tournament_disputes td
            JOIN public.brkt_matches bm ON bm.id = td.match_id
            JOIN public.team_members tm ON tm.team_id IN (bm.team1_id, bm.team2_id)
            WHERE td.id = dispute_comments.dispute_id
              AND tm.user_id = auth.uid()
              AND tm.is_active = true
        )
    )
    OR
    EXISTS (
        SELECT 1 FROM public.tournament_disputes td
        JOIN public.tournaments t ON t.id = td.tournament_id
        WHERE td.id = dispute_comments.dispute_id
          AND (t.organizer_id = auth.uid() OR td.assigned_to_user_id = auth.uid())
    )
    OR
    -- Organization staff with disputes:assist
    EXISTS (
        SELECT 1 FROM public.tournament_disputes td
        JOIN public.organization_staff os
          ON os.organization_id = (SELECT organization_id FROM tournaments WHERE id = td.tournament_id)
        JOIN public.staff_tournament_assignments sta
          ON sta.organization_staff_id = os.id AND sta.tournament_id = td.tournament_id
        WHERE td.id = dispute_comments.dispute_id
          AND os.user_id = auth.uid()
          AND os.status = 'active'
          AND 'disputes:assist' = ANY(os.permissions)
    )
    OR
    EXISTS (
        SELECT 1 FROM public.profiles p
        WHERE p.id = auth.uid()
          AND ('moderator' = ANY(p.admin_roles) OR 'ops_admin' = ANY(p.admin_roles))
    )
    OR
    EXISTS (
        SELECT 1 FROM public.admin_user_roles aur
        JOIN public.admin_roles ar ON ar.id = aur.role_id
        WHERE aur.user_id = auth.uid()
          AND lower(ar.name) = ANY(ARRAY['moderator', 'ops_admin'])
    )
    OR
    (SELECT auth.role()) = 'service_role'
);

-- ── Drop tournament_staff table ────────────────────────────────────────────
DROP TABLE IF EXISTS public.tournament_staff CASCADE;
