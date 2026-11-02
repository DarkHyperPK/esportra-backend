-- Links a provider match to the Esportra scrim map or tournament report it settles, preserves the rosters as
-- they were when it was reported, and keeps an append-only log of every association change.
-- context_id is the scrim_session_games id, or for tournaments a stable id derived from (match, game number) so both
-- teams' reports of one game share one association; tournament_match_id/game_number carry the readable reference.
-- team1_id / team2_id are competitor ids (a team, or a solo participant in tournaments): no FK on purpose.
CREATE TABLE IF NOT EXISTS public.provider_match_associations (
    id                    uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    context_type          text        NOT NULL,
    context_id            uuid        NOT NULL,
    tournament_match_id   uuid,
    game_number           integer,
    match_id              uuid        NOT NULL REFERENCES public.provider_matches(id),
    team1_id              uuid        NOT NULL,
    team2_id              uuid        NOT NULL,
    reported_team1_score  integer,
    reported_team2_score  integer,
    reported_map          text,
    team1_riot_side       text,
    team2_riot_side       text,
    method                text        NOT NULL DEFAULT 'reported',
    associated_by         uuid,
    verification_status   text        NOT NULL DEFAULT 'pending',
    verification_detail   jsonb       NOT NULL DEFAULT '{}'::jsonb,
    verified_at           timestamptz,
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at            timestamptz NOT NULL DEFAULT now()
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_pma_context') THEN
        ALTER TABLE public.provider_match_associations
            ADD CONSTRAINT uq_pma_context UNIQUE (context_type, context_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_pma_context_type') THEN
        ALTER TABLE public.provider_match_associations
            ADD CONSTRAINT chk_pma_context_type CHECK (context_type IN ('scrim_game', 'tournament_game'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_pma_method') THEN
        ALTER TABLE public.provider_match_associations
            ADD CONSTRAINT chk_pma_method CHECK (method IN ('reported', 'manual'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_pma_verification') THEN
        ALTER TABLE public.provider_match_associations
            ADD CONSTRAINT chk_pma_verification CHECK (verification_status IN
                ('pending', 'verified', 'partially_verified', 'unverified', 'disputed', 'duplicate', 'unavailable'));
    END IF;
END $$;

-- One Riot match counts once: a second context claiming it is stored as 'duplicate' and excluded here.
CREATE UNIQUE INDEX IF NOT EXISTS uq_pma_match_once
    ON public.provider_match_associations (match_id) WHERE verification_status <> 'duplicate';
CREATE INDEX IF NOT EXISTS idx_pma_team1 ON public.provider_match_associations (team1_id);
CREATE INDEX IF NOT EXISTS idx_pma_team2 ON public.provider_match_associations (team2_id);

CREATE TABLE IF NOT EXISTS public.provider_match_association_events (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    association_id  uuid        NOT NULL REFERENCES public.provider_match_associations(id),
    event_type      text        NOT NULL,
    actor_user_id   uuid,
    before_state    jsonb,
    after_state     jsonb,
    created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_pmae_association ON public.provider_match_association_events (association_id, created_at);

CREATE OR REPLACE FUNCTION public.provider_match_events_append_only() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'provider_match_association_events is append-only';
END;
$$;

DROP TRIGGER IF EXISTS trg_pmae_append_only ON public.provider_match_association_events;
CREATE TRIGGER trg_pmae_append_only
    BEFORE UPDATE OR DELETE ON public.provider_match_association_events
    FOR EACH ROW EXECUTE FUNCTION public.provider_match_events_append_only();

-- Roster as it was when the result was reported. Identity columns are written once; only the match
-- resolution columns (played, riot_side, riot_name) are filled in later, when the Riot data arrives.
CREATE TABLE IF NOT EXISTS public.provider_match_roster_snapshots (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    association_id   uuid        NOT NULL REFERENCES public.provider_match_associations(id),
    team_slot        smallint,
    team_id          uuid,
    user_id          uuid,
    puuid            text,
    member_role      text,
    identity_status  text        NOT NULL,
    played           boolean,
    riot_side        text,
    riot_name        text,
    snapshot_at      timestamptz NOT NULL DEFAULT now()
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_pmrs_identity') THEN
        ALTER TABLE public.provider_match_roster_snapshots
            ADD CONSTRAINT chk_pmrs_identity CHECK (identity_status IN ('linked', 'unlinked', 'unmatched'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_pmrs_slot') THEN
        ALTER TABLE public.provider_match_roster_snapshots
            ADD CONSTRAINT chk_pmrs_slot CHECK (team_slot IS NULL OR team_slot IN (1, 2));
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS uq_pmrs_user
    ON public.provider_match_roster_snapshots (association_id, user_id) WHERE user_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS uq_pmrs_puuid_only
    ON public.provider_match_roster_snapshots (association_id, puuid) WHERE user_id IS NULL AND puuid IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_pmrs_user ON public.provider_match_roster_snapshots (user_id) WHERE user_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_pmrs_puuid ON public.provider_match_roster_snapshots (puuid) WHERE puuid IS NOT NULL;

ALTER TABLE public.provider_match_associations ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.provider_match_association_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.provider_match_roster_snapshots ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS pma_service ON public.provider_match_associations;
CREATE POLICY pma_service ON public.provider_match_associations FOR ALL TO service_role USING (true) WITH CHECK (true);
DROP POLICY IF EXISTS pmae_service ON public.provider_match_association_events;
CREATE POLICY pmae_service ON public.provider_match_association_events FOR ALL TO service_role USING (true) WITH CHECK (true);
DROP POLICY IF EXISTS pmrs_service ON public.provider_match_roster_snapshots;
CREATE POLICY pmrs_service ON public.provider_match_roster_snapshots FOR ALL TO service_role USING (true) WITH CHECK (true);

REVOKE ALL ON public.provider_match_associations FROM anon, authenticated;
REVOKE ALL ON public.provider_match_association_events FROM anon, authenticated;
REVOKE ALL ON public.provider_match_roster_snapshots FROM anon, authenticated;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.provider_match_associations TO service_role;
GRANT SELECT, INSERT ON public.provider_match_association_events TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.provider_match_roster_snapshots TO service_role;
