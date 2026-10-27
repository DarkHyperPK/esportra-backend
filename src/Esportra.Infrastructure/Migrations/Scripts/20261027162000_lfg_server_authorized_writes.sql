-- LFG mutations pass through the API, which verifies listing ownership,
-- team authorization, state transitions and limits. Direct client writes
-- cannot safely enforce these checks with the previous broad policies.
DROP POLICY IF EXISTS lfg_own ON public.lfg_listings;
DROP POLICY IF EXISTS lfg_owner_select ON public.lfg_listings;
CREATE POLICY lfg_owner_select ON public.lfg_listings FOR SELECT TO authenticated
    USING (user_id = auth.uid());
DROP POLICY IF EXISTS lfg_public ON public.lfg_listings;
CREATE POLICY lfg_public ON public.lfg_listings FOR SELECT TO authenticated
    USING (status = 'published' AND (expires_at IS NULL OR expires_at > now()));
REVOKE INSERT, UPDATE, DELETE ON public.lfg_listings FROM authenticated;
GRANT SELECT ON public.lfg_listings TO authenticated;

-- Parties may still read their own interests. The API owns all transitions,
-- ensuring that clients cannot rewrite the listing or recipient on a row.
DROP POLICY IF EXISTS lfgi_insert ON public.lfg_interests;
DROP POLICY IF EXISTS lfgi_sender_update ON public.lfg_interests;
DROP POLICY IF EXISTS lfgi_owner_update ON public.lfg_interests;
REVOKE INSERT, UPDATE, DELETE ON public.lfg_interests FROM authenticated;
GRANT SELECT ON public.lfg_interests TO authenticated;
