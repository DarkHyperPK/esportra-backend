-- Security: riot_accounts was readable by every authenticated user (and, through riot_accounts_public_select,
-- by anyone holding the anon key). The PUUID <-> user mapping is the key to a player's Riot match history and is
-- only read by the backend (service connection) or by the owner. Same for the PUUID name history.
-- The API exposes the public Riot ID where a player allows it (privacy_settings.show_riot_account).

DROP POLICY IF EXISTS riot_accounts_public_select ON public.riot_accounts;
DROP POLICY IF EXISTS riot_accounts_select_policy ON public.riot_accounts;
DROP POLICY IF EXISTS riot_accounts_insert_policy ON public.riot_accounts;
DROP POLICY IF EXISTS riot_accounts_update_policy ON public.riot_accounts;
DROP POLICY IF EXISTS riot_accounts_delete_policy ON public.riot_accounts;
DROP POLICY IF EXISTS riot_accounts_owner_select ON public.riot_accounts;
DROP POLICY IF EXISTS riot_accounts_service ON public.riot_accounts;

ALTER TABLE public.riot_accounts ENABLE ROW LEVEL SECURITY;

CREATE POLICY riot_accounts_owner_select ON public.riot_accounts
    FOR SELECT TO authenticated USING (user_id = auth.uid());
CREATE POLICY riot_accounts_service ON public.riot_accounts
    FOR ALL TO service_role USING (true) WITH CHECK (true);

-- Linking, renaming and unlinking all go through the API; clients never write this table directly.
REVOKE ALL ON public.riot_accounts FROM anon;
REVOKE INSERT, UPDATE, DELETE ON public.riot_accounts FROM authenticated;

DROP POLICY IF EXISTS riot_name_history_public_select ON public.riot_account_name_history;
DROP POLICY IF EXISTS riot_name_history_service ON public.riot_account_name_history;
ALTER TABLE public.riot_account_name_history ENABLE ROW LEVEL SECURITY;
CREATE POLICY riot_name_history_service ON public.riot_account_name_history
    FOR ALL TO service_role USING (true) WITH CHECK (true);
REVOKE ALL ON public.riot_account_name_history FROM anon, authenticated;
