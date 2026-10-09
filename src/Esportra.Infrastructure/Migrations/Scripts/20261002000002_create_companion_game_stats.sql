-- Free-form per-game stats tracked by the companion app, independent of tournament matches.
-- Every Valorant game the companion detects gets a row here, regardless of tournament state.
CREATE TABLE IF NOT EXISTS public.companion_game_stats (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id         uuid        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    riot_match_id   text,
    agent           text,
    map             text,
    outcome         text        CHECK (outcome IN ('win', 'loss', 'draw')),
    kills           int         NOT NULL DEFAULT 0,
    deaths          int         NOT NULL DEFAULT 0,
    assists         int         NOT NULL DEFAULT 0,
    round_stats     jsonb,
    collected_at    timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE public.companion_game_stats ENABLE ROW LEVEL SECURITY;

-- Idempotent upsert key: one row per (user, riot match)
CREATE UNIQUE INDEX IF NOT EXISTS companion_game_stats_user_riot_uniq
    ON public.companion_game_stats (user_id, riot_match_id)
    WHERE riot_match_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS companion_game_stats_user_created_idx
    ON public.companion_game_stats (user_id, created_at DESC);

-- Users see only their own rows
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'companion_game_stats' AND policyname = 'own_stats'
  ) THEN
    CREATE POLICY own_stats ON public.companion_game_stats
      FOR ALL TO authenticated
      USING   (user_id = auth.uid())
      WITH CHECK (user_id = auth.uid());
  END IF;
END $$;
