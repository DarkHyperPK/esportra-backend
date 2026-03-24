-- Fix dispute_comments RLS policies so that:
-- INSERT: both captains in the match (not just the one who filed) + organizer + staff + admin
-- SELECT: both captains + organizer + staff + admin (non-internal only for captains)

-- ── INSERT policy ─────────────────────────────────────────────────────────

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
        -- (covers the other captain whose result was disputed)
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
        -- Tournament staff with disputes:assist permission
        EXISTS (
            SELECT 1 FROM public.tournament_disputes td
            JOIN public.tournament_staff ts ON ts.tournament_id = td.tournament_id
            WHERE td.id = dispute_comments.dispute_id
              AND ts.user_id = auth.uid()
              AND ts.status = 'active'
              AND 'disputes:assist' = ANY(ts.permissions)
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

-- ── SELECT policy ─────────────────────────────────────────────────────────
-- Allow both match participants to see non-internal comments;
-- organizers/staff/admins can see all (including is_internal = true).

DROP POLICY IF EXISTS dc_select_own_or_dispute ON public.dispute_comments;

CREATE POLICY dc_select_own_or_dispute ON public.dispute_comments
FOR SELECT USING (
    -- Own comment
    user_id = auth.uid()
    OR
    -- Non-internal comment visible to any match participant (both teams)
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
    -- Tournament organizer or assigned reviewer (sees all, including internal)
    EXISTS (
        SELECT 1 FROM public.tournament_disputes td
        JOIN public.tournaments t ON t.id = td.tournament_id
        WHERE td.id = dispute_comments.dispute_id
          AND (t.organizer_id = auth.uid() OR td.assigned_to_user_id = auth.uid())
    )
    OR
    -- Tournament staff with disputes:assist
    EXISTS (
        SELECT 1 FROM public.tournament_disputes td
        JOIN public.tournament_staff ts ON ts.tournament_id = td.tournament_id
        WHERE td.id = dispute_comments.dispute_id
          AND ts.user_id = auth.uid()
          AND ts.status = 'active'
          AND 'disputes:assist' = ANY(ts.permissions)
    )
    OR
    -- Admin/moderator via profiles
    EXISTS (
        SELECT 1 FROM public.profiles p
        WHERE p.id = auth.uid()
          AND ('moderator' = ANY(p.admin_roles) OR 'ops_admin' = ANY(p.admin_roles))
    )
    OR
    -- Admin/moderator via admin_user_roles
    EXISTS (
        SELECT 1 FROM public.admin_user_roles aur
        JOIN public.admin_roles ar ON ar.id = aur.role_id
        WHERE aur.user_id = auth.uid()
          AND lower(ar.name) = ANY(ARRAY['moderator', 'ops_admin'])
    )
    OR
    (SELECT auth.role()) = 'service_role'
);
