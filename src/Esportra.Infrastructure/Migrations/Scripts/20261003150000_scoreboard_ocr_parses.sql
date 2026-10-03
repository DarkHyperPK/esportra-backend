-- Screenshot scoreboard OCR (Valorant v1).
-- scoreboard_ocr_parses keeps the raw, server-authored OCR output for every parse so
-- organizers can compare it with what the captain finally submitted. Rows are written
-- and read only by the API (service connection); clients never touch the table directly.
CREATE TABLE IF NOT EXISTS public.scoreboard_ocr_parses (
    id                  uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    match_id            uuid        NOT NULL REFERENCES public.brkt_matches(id) ON DELETE CASCADE,
    game_number         int         NOT NULL DEFAULT 1,
    requested_by        uuid        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    competitor_id       uuid        NOT NULL,
    game                text        NOT NULL DEFAULT 'valorant' CHECK (game IN ('valorant')),
    screenshot_path     text        NOT NULL,
    screenshot_sha256   text        NOT NULL,
    result              jsonb       NOT NULL,
    engine_version      text,
    created_at          timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE public.scoreboard_ocr_parses ENABLE ROW LEVEL SECURITY;

-- Default deny: no policies for anon/authenticated. Access is through the API only.

CREATE INDEX IF NOT EXISTS scoreboard_ocr_parses_match_idx
    ON public.scoreboard_ocr_parses (match_id, game_number, created_at DESC);

ALTER TABLE public.match_result_reports
    ADD COLUMN IF NOT EXISTS source       text,
    ADD COLUMN IF NOT EXISTS ocr_parse_id uuid REFERENCES public.scoreboard_ocr_parses(id) ON DELETE SET NULL;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'match_result_reports_source_check') THEN
    ALTER TABLE public.match_result_reports
      ADD CONSTRAINT match_result_reports_source_check
      CHECK (source IS NULL OR source IN ('riot', 'manual', 'ocr'));
  END IF;
END $$;

-- source = 'ocr' requires ocr_parse_id at insert time; enforced in the API rather than a
-- CHECK so that ON DELETE SET NULL on the parse row can never block a cascade.
