-- Columns used by RiotIdRefreshService to track when a linked account's name was last verified.
ALTER TABLE public.riot_accounts ADD COLUMN IF NOT EXISTS name_checked_at timestamptz;
ALTER TABLE public.riot_accounts ADD COLUMN IF NOT EXISTS updated_at      timestamptz;

-- History of previous Riot IDs for a PUUID so old screenshots / match reports still resolve.
CREATE TABLE IF NOT EXISTS public.riot_account_name_history (
    id          uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    puuid       text        NOT NULL,
    game_name   text        NOT NULL,
    tag_line    text        NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS riot_account_name_history_puuid_idx ON public.riot_account_name_history (puuid);

ALTER TABLE public.riot_account_name_history ENABLE ROW LEVEL SECURITY;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'riot_account_name_history' AND policyname = 'riot_name_history_public_select'
  ) THEN
    CREATE POLICY "riot_name_history_public_select"
      ON public.riot_account_name_history FOR SELECT USING (true);
  END IF;
END $$;
