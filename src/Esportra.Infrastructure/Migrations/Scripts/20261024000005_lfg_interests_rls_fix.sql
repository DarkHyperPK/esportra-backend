-- PROJ-065 Security fix: tighten lfg_interests RLS.
-- The original lfgi_parties FOR ALL policy allowed the interest sender to UPDATE
-- a row's status to 'accepted' directly via PostgREST, bypassing listing-owner consent.
-- Split into three narrow policies:
--   lfgi_select  — both parties may read
--   lfgi_insert  — sender may insert (from_user_id = auth.uid())
--   lfgi_sender_update  — sender may only withdraw (set status to 'withdrawn')
--   lfgi_owner_update   — listing owner (to_user_id) may accept or decline
--
-- The API layer (AcceptInterestAsync / DeclineInterestAsync) already enforces
-- to_user_id = auth.uid() before the UPDATE; this policy makes it airtight at
-- the database level as well.

DROP POLICY IF EXISTS lfgi_parties    ON public.lfg_interests;
DROP POLICY IF EXISTS lfgi_service    ON public.lfg_interests;

-- Both parties may read their own interests.
DROP POLICY IF EXISTS lfgi_select ON public.lfg_interests;
CREATE POLICY lfgi_select
    ON public.lfg_interests
    FOR SELECT
    TO authenticated
    USING (from_user_id = auth.uid() OR to_user_id = auth.uid());

-- Only the sender may insert a new interest.
DROP POLICY IF EXISTS lfgi_insert ON public.lfg_interests;
CREATE POLICY lfgi_insert
    ON public.lfg_interests
    FOR INSERT
    TO authenticated
    WITH CHECK (from_user_id = auth.uid());

-- Sender may withdraw: set status = 'withdrawn' only.
-- The CHECK enforces that from_user_id is unchanged and status is 'withdrawn'.
DROP POLICY IF EXISTS lfgi_sender_update ON public.lfg_interests;
CREATE POLICY lfgi_sender_update
    ON public.lfg_interests
    FOR UPDATE
    TO authenticated
    USING  (from_user_id = auth.uid())
    WITH CHECK (from_user_id = auth.uid() AND status = 'withdrawn');

-- Listing owner may accept or decline: status must be 'accepted' or 'declined'.
DROP POLICY IF EXISTS lfgi_owner_update ON public.lfg_interests;
CREATE POLICY lfgi_owner_update
    ON public.lfg_interests
    FOR UPDATE
    TO authenticated
    USING  (to_user_id = auth.uid())
    WITH CHECK (to_user_id = auth.uid() AND status IN ('accepted', 'declined'));

-- Service role: unrestricted.
DROP POLICY IF EXISTS lfgi_service ON public.lfg_interests;
CREATE POLICY lfgi_service
    ON public.lfg_interests
    FOR ALL
    TO service_role
    USING (true)
    WITH CHECK (true);
