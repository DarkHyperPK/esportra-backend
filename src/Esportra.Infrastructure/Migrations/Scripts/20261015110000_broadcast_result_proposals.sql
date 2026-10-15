-- A paired node may propose a result; only an active, authorized crew member may apply it.
CREATE TABLE IF NOT EXISTS public.broadcast_result_proposals (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    show_id uuid NOT NULL REFERENCES public.broadcast_shows(id) ON DELETE CASCADE,
    match_id uuid NOT NULL REFERENCES public.brkt_matches(id),
    node_id uuid REFERENCES public.broadcast_nodes(id) ON DELETE SET NULL,
    client_key uuid NOT NULL,
    team1_id uuid NOT NULL,
    team2_id uuid NOT NULL,
    best_of integer NOT NULL CHECK (best_of BETWEEN 1 AND 5),
    map_number integer NOT NULL CHECK (map_number BETWEEN 1 AND 5),
    team1_score integer NOT NULL CHECK (team1_score >= 0),
    team2_score integer NOT NULL CHECK (team2_score >= 0),
    reason text NOT NULL CHECK (length(reason) BETWEEN 1 AND 500),
    status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'applied', 'rejected')),
    reviewed_by uuid REFERENCES auth.users(id),
    reviewed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (show_id, client_key)
);
CREATE INDEX IF NOT EXISTS broadcast_result_proposals_show_created_idx
    ON public.broadcast_result_proposals (show_id, created_at DESC);
ALTER TABLE public.broadcast_result_proposals ENABLE ROW LEVEL SECURITY;
-- Direct browser writes are forbidden. The API enforces can_submit_result for review.
CREATE POLICY broadcast_result_proposals_read ON public.broadcast_result_proposals
    FOR SELECT TO authenticated USING (
        EXISTS (SELECT 1 FROM public.broadcast_shows bs JOIN public.organizations o ON o.id = bs.org_id
                WHERE bs.id = show_id AND o.owner_id = auth.uid())
        OR EXISTS (SELECT 1 FROM public.broadcast_crew bc
                   WHERE bc.show_id = broadcast_result_proposals.show_id AND bc.user_id = auth.uid() AND bc.is_active = true)
    );
GRANT SELECT ON public.broadcast_result_proposals TO authenticated;
GRANT ALL ON public.broadcast_result_proposals TO service_role;
