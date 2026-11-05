-- Squad Up session filters: what the player wants to play (mode), where they are (profile country),
-- which countries they want to meet, an optional rank range, and activity for the 10-minute idle rule.
-- Sessions are ephemeral (stale rows are deleted on the next start), so defaults are enough; no backfill.
ALTER TABLE public.lfg_swipe_sessions ADD COLUMN IF NOT EXISTS mode TEXT NOT NULL DEFAULT 'competitive';
ALTER TABLE public.lfg_swipe_sessions ADD COLUMN IF NOT EXISTS country_code TEXT;
ALTER TABLE public.lfg_swipe_sessions ADD COLUMN IF NOT EXISTS wanted_countries TEXT[] NOT NULL DEFAULT '{}';
ALTER TABLE public.lfg_swipe_sessions ADD COLUMN IF NOT EXISTS min_tier INT;
ALTER TABLE public.lfg_swipe_sessions ADD COLUMN IF NOT EXISTS max_tier INT;
ALTER TABLE public.lfg_swipe_sessions ADD COLUMN IF NOT EXISTS last_activity_at TIMESTAMPTZ NOT NULL DEFAULT now();

-- Unrated sessions may be played by players without a current rank.
ALTER TABLE public.lfg_swipe_sessions ALTER COLUMN competitive_tier DROP NOT NULL;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_lfg_swipe_sessions_mode') THEN
    ALTER TABLE public.lfg_swipe_sessions ADD CONSTRAINT chk_lfg_swipe_sessions_mode CHECK (mode IN ('competitive', 'unrated'));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_lfg_swipe_sessions_rank_range') THEN
    ALTER TABLE public.lfg_swipe_sessions ADD CONSTRAINT chk_lfg_swipe_sessions_rank_range
      CHECK (min_tier IS NULL OR max_tier IS NULL OR min_tier <= max_tier);
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_lfg_swipe_sessions_pool_mode
    ON public.lfg_swipe_sessions (game_slug, region, mode, last_heartbeat DESC);
