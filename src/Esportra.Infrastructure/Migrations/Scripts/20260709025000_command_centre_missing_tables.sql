-- Migration: Create missing tables for Command Centre endpoint
-- The command-centre endpoint queries disputes and admin_session_audit tables that don't exist.

-- 1. Create disputes table (general platform disputes, separate from match_disputes)
-- This is for tournament-level and platform-level disputes, not match result disputes.
CREATE TABLE IF NOT EXISTS public.disputes (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tournament_id UUID REFERENCES public.tournaments(id) ON DELETE CASCADE,
    match_id UUID REFERENCES public.brkt_matches(id) ON DELETE CASCADE,
    reporter_id UUID REFERENCES public.profiles(id) ON DELETE SET NULL,
    status TEXT NOT NULL DEFAULT 'open'
        CHECK (status IN ('open', 'in_progress', 'resolved', 'closed')),
    priority TEXT DEFAULT 'normal'
        CHECK (priority IN ('low', 'normal', 'high', 'critical')),
    category TEXT,
    title TEXT,
    description TEXT,
    resolution TEXT,
    resolved_by UUID REFERENCES public.profiles(id) ON DELETE SET NULL,
    resolved_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_disputes_status ON public.disputes(status);
CREATE INDEX IF NOT EXISTS idx_disputes_priority ON public.disputes(priority);
CREATE INDEX IF NOT EXISTS idx_disputes_tournament ON public.disputes(tournament_id);
CREATE INDEX IF NOT EXISTS idx_disputes_created ON public.disputes(created_at DESC);

-- RLS for disputes
ALTER TABLE public.disputes ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS disputes_admin_all ON public.disputes;
CREATE POLICY disputes_admin_all ON public.disputes
    FOR ALL TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.admin_users au
            WHERE au.user_id = auth.uid()
        )
    );

DROP POLICY IF EXISTS disputes_reporter_read ON public.disputes;
CREATE POLICY disputes_reporter_read ON public.disputes
    FOR SELECT TO authenticated
    USING (reporter_id = auth.uid());

DROP POLICY IF EXISTS disputes_authenticated_insert ON public.disputes;
CREATE POLICY disputes_authenticated_insert ON public.disputes
    FOR INSERT TO authenticated
    WITH CHECK (reporter_id = auth.uid());


-- 2. Create admin_session_audit table for tracking active admin sessions
CREATE TABLE IF NOT EXISTS public.admin_session_audit (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL REFERENCES public.profiles(id) ON DELETE CASCADE,
    session_id TEXT,
    action TEXT NOT NULL CHECK (action IN ('login', 'logout', 'activity', 'refresh')),
    ip_address INET,
    user_agent TEXT,
    metadata JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_admin_session_audit_user ON public.admin_session_audit(user_id);
CREATE INDEX IF NOT EXISTS idx_admin_session_audit_created ON public.admin_session_audit(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_admin_session_audit_recent ON public.admin_session_audit(user_id, created_at DESC)
    WHERE created_at >= NOW() - INTERVAL '15 minutes';

-- RLS for admin_session_audit
ALTER TABLE public.admin_session_audit ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS admin_session_audit_admin_read ON public.admin_session_audit;
CREATE POLICY admin_session_audit_admin_read ON public.admin_session_audit
    FOR SELECT TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.admin_users au
            WHERE au.user_id = auth.uid()
        )
    );

DROP POLICY IF EXISTS admin_session_audit_self_insert ON public.admin_session_audit;
CREATE POLICY admin_session_audit_self_insert ON public.admin_session_audit
    FOR INSERT TO authenticated
    WITH CHECK (user_id = auth.uid());
