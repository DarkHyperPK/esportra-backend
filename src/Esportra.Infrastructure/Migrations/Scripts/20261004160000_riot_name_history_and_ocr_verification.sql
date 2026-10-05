-- Riot ID freshness and screenshot-report player verification.
--
-- Riot IDs (game name + tag) can be changed by players at any time; the PUUID never changes.
-- The API re-reads linked accounts by PUUID and keeps the names it replaced, so a screenshot taken
-- shortly before a rename still verifies. Written and read only by the API (service connection).
CREATE TABLE IF NOT EXISTS public.riot_account_name_history (
    id          uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    puuid       text        NOT NULL,
    game_name   text        NOT NULL,
    tag_line    text        NOT NULL,
    seen_until  timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE public.riot_account_name_history ENABLE ROW LEVEL SECURITY;

-- Default deny: no policies for anon/authenticated. Access is through the API only.

CREATE INDEX IF NOT EXISTS riot_account_name_history_puuid_idx
    ON public.riot_account_name_history (puuid, seen_until DESC);

-- When the Riot name was last checked against Riot (updated_at also moves on token refreshes).
ALTER TABLE public.riot_accounts
    ADD COLUMN IF NOT EXISTS name_checked_at timestamptz;

CREATE INDEX IF NOT EXISTS riot_accounts_name_checked_idx
    ON public.riot_accounts (name_checked_at NULLS FIRST);

-- Per-player verification of a screenshot report, computed by the API at submit time.
ALTER TABLE public.match_result_reports
    ADD COLUMN IF NOT EXISTS ocr_verification jsonb;

-- The same screenshot reused for another match is flagged.
CREATE INDEX IF NOT EXISTS scoreboard_ocr_parses_sha_idx
    ON public.scoreboard_ocr_parses (screenshot_sha256);
