-- Phase 1 competitive intelligence: one row per provider match (Riot match id), stored once, plus the raw
-- response so analytics can be recalculated when the parser changes. Written only by the backend.
CREATE TABLE IF NOT EXISTS public.provider_matches (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    game_slug          text        NOT NULL DEFAULT 'valorant',
    provider           text        NOT NULL DEFAULT 'riot',
    provider_match_id  text        NOT NULL,
    map_provider_id    text,
    queue_id           text,
    is_custom          boolean,
    game_mode          text,
    started_at         timestamptz,
    length_ms          integer,
    shard              text,
    game_version       text,
    season_id          text,
    is_completed       boolean,
    data_status        text        NOT NULL DEFAULT 'pending',
    status_detail      text,
    parser_version     integer,
    fetch_attempts     integer     NOT NULL DEFAULT 0,
    ingested_at        timestamptz,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now()
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_provider_matches_provider_id') THEN
        ALTER TABLE public.provider_matches
            ADD CONSTRAINT uq_provider_matches_provider_id UNIQUE (provider, provider_match_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_provider_matches_status') THEN
        ALTER TABLE public.provider_matches
            ADD CONSTRAINT chk_provider_matches_status CHECK (data_status IN
                ('pending', 'retrieved', 'incomplete', 'unavailable', 'failed'));
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_provider_matches_started ON public.provider_matches (started_at DESC);
CREATE INDEX IF NOT EXISTS idx_provider_matches_pending ON public.provider_matches (data_status)
    WHERE data_status IN ('pending', 'incomplete');

CREATE TABLE IF NOT EXISTS public.provider_match_payloads (
    match_id        uuid        PRIMARY KEY REFERENCES public.provider_matches(id) ON DELETE CASCADE,
    payload         jsonb       NOT NULL,
    payload_sha256  text        NOT NULL,
    fetched_at      timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE public.provider_matches ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.provider_match_payloads ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS provider_matches_service ON public.provider_matches;
CREATE POLICY provider_matches_service ON public.provider_matches
    FOR ALL TO service_role USING (true) WITH CHECK (true);
DROP POLICY IF EXISTS provider_match_payloads_service ON public.provider_match_payloads;
CREATE POLICY provider_match_payloads_service ON public.provider_match_payloads
    FOR ALL TO service_role USING (true) WITH CHECK (true);

REVOKE ALL ON public.provider_matches FROM anon, authenticated;
REVOKE ALL ON public.provider_match_payloads FROM anon, authenticated;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.provider_matches TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.provider_match_payloads TO service_role;
