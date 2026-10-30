-- Per-player refresh window shared by automatic jobs and authenticated listing viewers.
ALTER TABLE public.player_game_stats ADD COLUMN IF NOT EXISTS sync_requested_at timestamptz;
-- An initialized Valorant row has not necessarily completed a provider fetch.
-- Keep the existing default for other integrations; Valorant explicitly inserts NULL.
ALTER TABLE public.player_game_stats ALTER COLUMN synced_at DROP NOT NULL;
