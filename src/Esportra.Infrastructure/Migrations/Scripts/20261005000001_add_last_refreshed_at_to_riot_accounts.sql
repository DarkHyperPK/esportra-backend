ALTER TABLE public.riot_accounts ADD COLUMN IF NOT EXISTS last_refreshed_at timestamptz;
