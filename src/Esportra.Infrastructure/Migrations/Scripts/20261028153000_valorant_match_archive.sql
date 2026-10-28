-- Private archive for match data explicitly linked to one Esportra user.
CREATE TABLE IF NOT EXISTS public.valorant_act_syncs (
    user_id uuid NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    provider_player_id text NOT NULL,
    act_id text NOT NULL,
    starts_at timestamptz NOT NULL,
    is_current boolean NOT NULL,
    sync_status text NOT NULL DEFAULT 'pending',
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, provider_player_id, act_id)
);
CREATE TABLE IF NOT EXISTS public.valorant_player_matches (
    user_id uuid NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    provider_player_id text NOT NULL,
    match_id text NOT NULL,
    act_id text NOT NULL,
    started_at timestamptz NOT NULL,
    queue_id text,
    agent_id text,
    won boolean,
    kills integer NOT NULL,
    deaths integer NOT NULL,
    assists integer NOT NULL,
    score integer NOT NULL,
    competitive_tier integer,
    PRIMARY KEY (user_id, provider_player_id, match_id)
);
-- Persist discovered IDs so a transient failure cannot lose a match when Riot's list changes.
CREATE TABLE IF NOT EXISTS public.valorant_match_backlog (
    user_id uuid NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    provider_player_id text NOT NULL,
    match_id text NOT NULL,
    match_started_at timestamptz,
    discovered_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, provider_player_id, match_id)
);
ALTER TABLE public.valorant_match_backlog ADD COLUMN IF NOT EXISTS match_started_at timestamptz;
CREATE INDEX IF NOT EXISTS idx_valorant_matches_user_act
    ON public.valorant_player_matches (user_id, provider_player_id, act_id, started_at DESC);
ALTER TABLE public.valorant_act_syncs ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.valorant_player_matches ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.valorant_match_backlog ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS valorant_act_sync_service ON public.valorant_act_syncs;
CREATE POLICY valorant_act_sync_service ON public.valorant_act_syncs FOR ALL TO service_role USING (true) WITH CHECK (true);
DROP POLICY IF EXISTS valorant_match_archive_service ON public.valorant_player_matches;
CREATE POLICY valorant_match_archive_service ON public.valorant_player_matches FOR ALL TO service_role USING (true) WITH CHECK (true);
REVOKE ALL ON public.valorant_act_syncs FROM anon, authenticated;
REVOKE ALL ON public.valorant_player_matches FROM anon, authenticated;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.valorant_act_syncs TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.valorant_player_matches TO service_role;

DROP POLICY IF EXISTS valorant_match_backlog_service ON public.valorant_match_backlog;
CREATE POLICY valorant_match_backlog_service ON public.valorant_match_backlog FOR ALL TO service_role USING (true) WITH CHECK (true);
REVOKE ALL ON public.valorant_match_backlog FROM anon, authenticated;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.valorant_match_backlog TO service_role;
