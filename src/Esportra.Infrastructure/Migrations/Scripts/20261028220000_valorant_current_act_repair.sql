-- Additive repair for databases that already journaled earlier LFG migrations.
ALTER TABLE public.valorant_player_matches
    ADD COLUMN IF NOT EXISTS headshots integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS shots integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS player_card_id text,
    ADD COLUMN IF NOT EXISTS headshot_data_complete boolean,
    ADD COLUMN IF NOT EXISTS match_data_version integer NOT NULL DEFAULT 0;
ALTER TABLE public.player_game_stats
    ADD COLUMN IF NOT EXISTS total_headshots integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS total_shots integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS hs_pct numeric,
    ADD COLUMN IF NOT EXISTS player_card_id text,
    ADD COLUMN IF NOT EXISTS headshot_data_complete boolean NOT NULL DEFAULT false;

-- Retain legacy aggregates when their account or act changes.
CREATE TABLE IF NOT EXISTS public.valorant_aggregate_snapshots (
    user_id uuid NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    provider_player_id text NOT NULL,
    act_id text NOT NULL,
    snapshot jsonb NOT NULL,
    captured_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, provider_player_id, act_id)
);
ALTER TABLE public.valorant_aggregate_snapshots ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS valorant_aggregate_snapshots_service ON public.valorant_aggregate_snapshots;
CREATE POLICY valorant_aggregate_snapshots_service ON public.valorant_aggregate_snapshots
    FOR ALL TO service_role USING (true) WITH CHECK (true);
REVOKE ALL ON public.valorant_aggregate_snapshots FROM anon, authenticated;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.valorant_aggregate_snapshots TO service_role;
