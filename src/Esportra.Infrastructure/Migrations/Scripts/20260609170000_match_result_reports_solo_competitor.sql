-- match_result_reports stores bracket competitor IDs (team UUID or solo participant UUID).
-- Repair schema drift, drop team-only FKs, dedupe, and ensure upsert key exists.

ALTER TABLE public.match_result_reports
    ADD COLUMN IF NOT EXISTS game_number INTEGER NOT NULL DEFAULT 1;

ALTER TABLE public.match_result_reports
    ADD COLUMN IF NOT EXISTS reported_by_team_id UUID;

ALTER TABLE public.match_result_reports
    ADD COLUMN IF NOT EXISTS team1_score INTEGER;

ALTER TABLE public.match_result_reports
    ADD COLUMN IF NOT EXISTS team2_score INTEGER;

ALTER TABLE public.match_result_reports
    ADD COLUMN IF NOT EXISTS winner_team_id UUID;

ALTER TABLE public.match_result_reports
    ADD COLUMN IF NOT EXISTS screenshot_urls JSONB DEFAULT '[]'::jsonb;

ALTER TABLE public.match_result_reports
    ADD COLUMN IF NOT EXISTS match_data JSONB DEFAULT '{}'::jsonb;

ALTER TABLE public.match_result_reports
    ADD COLUMN IF NOT EXISTS comment TEXT;

ALTER TABLE public.match_result_reports
    ADD COLUMN IF NOT EXISTS map_id UUID;

ALTER TABLE public.match_result_reports
    ADD COLUMN IF NOT EXISTS map_name TEXT;

ALTER TABLE public.match_result_reports
    ADD COLUMN IF NOT EXISTS riot_match_id TEXT;

ALTER TABLE public.match_result_reports
    ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ DEFAULT now();

ALTER TABLE public.match_result_reports
    ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ DEFAULT now();

-- Competitor columns may reference tournament_participants.id, not only teams.id
ALTER TABLE public.match_result_reports
    DROP CONSTRAINT IF EXISTS match_result_reports_reported_by_team_id_fkey;

ALTER TABLE public.match_result_reports
    DROP CONSTRAINT IF EXISTS match_result_reports_winner_team_id_fkey;

ALTER TABLE public.match_result_reports
    DROP CONSTRAINT IF EXISTS fk_match_result_reports_reported_by_team_id;

ALTER TABLE public.match_result_reports
    DROP CONSTRAINT IF EXISTS fk_match_result_reports_winner_team_id;

ALTER TABLE public.match_result_reports
    DROP CONSTRAINT IF EXISTS match_result_reports_team_id_fkey;

-- Remove duplicate rows before creating the upsert unique index
DELETE FROM public.match_result_reports a
USING public.match_result_reports b
WHERE a.id > b.id
  AND a.match_id IS NOT DISTINCT FROM b.match_id
  AND a.game_number IS NOT DISTINCT FROM b.game_number
  AND a.reported_by_team_id IS NOT DISTINCT FROM b.reported_by_team_id
  AND a.reported_by_team_id IS NOT NULL;

DROP INDEX IF EXISTS public.idx_match_result_reports_match_game_reporter;

CREATE UNIQUE INDEX idx_match_result_reports_match_game_reporter
    ON public.match_result_reports (match_id, game_number, reported_by_team_id);
