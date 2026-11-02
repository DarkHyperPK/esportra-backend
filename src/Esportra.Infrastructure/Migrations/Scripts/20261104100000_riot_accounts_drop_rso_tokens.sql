-- Security: Riot Sign On access/refresh tokens were stored in plain text in riot_accounts and were never read
-- after linking (the callback uses them once to call accounts/me). Stop retaining them.
--
-- PRE-FLIGHT: Run on production before deploying, confirm the count is plausible (one row per linked player):
-- SELECT count(*) FROM public.riot_accounts WHERE access_token IS NOT NULL OR refresh_token IS NOT NULL;
-- Expected: roughly the number of linked Riot accounts. Nothing reads these columns.
-- AFTER DEPLOY: rotate the Riot RSO client secret (tokens may have been readable through the data API).
UPDATE public.riot_accounts
   SET access_token = NULL, refresh_token = NULL, token_expires_at = NULL
 WHERE access_token IS NOT NULL OR refresh_token IS NOT NULL OR token_expires_at IS NOT NULL;

ALTER TABLE public.riot_accounts DROP COLUMN IF EXISTS access_token;
ALTER TABLE public.riot_accounts DROP COLUMN IF EXISTS refresh_token;
ALTER TABLE public.riot_accounts DROP COLUMN IF EXISTS token_expires_at;
