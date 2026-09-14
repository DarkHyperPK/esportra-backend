-- ============================================================================
-- Migration: Tournament External Participants
-- Purpose:   Allows API key holders (organizations) to register external
--            players in a tournament without requiring a Supabase auth account.
--            This is the Developer API surface — access is via service_role
--            only. There is intentionally no authenticated (JWT) SELECT policy;
--            JWT-authenticated users do not access this table.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. Create tournament_external_participants table
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.tournament_external_participants (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    tournament_id   UUID        NOT NULL REFERENCES public.tournaments(id) ON DELETE CASCADE,
    organization_id UUID        NOT NULL REFERENCES public.organizations(id),
    external_id     TEXT        NOT NULL,
    name            TEXT        NOT NULL,
    metadata        JSONB       NOT NULL DEFAULT '{}',
    seeding         INTEGER,
    placement       INTEGER,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ---------------------------------------------------------------------------
-- 2. Constraints (wrapped in DO $$ guard — ADD CONSTRAINT has no IF NOT EXISTS)
-- ---------------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'tournament_external_participants_external_id_len'
    ) THEN
        ALTER TABLE public.tournament_external_participants
            ADD CONSTRAINT tournament_external_participants_external_id_len
            CHECK (char_length(external_id) <= 255);
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'tournament_external_participants_name_len'
    ) THEN
        ALTER TABLE public.tournament_external_participants
            ADD CONSTRAINT tournament_external_participants_name_len
            CHECK (char_length(name) <= 255);
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- 3. Indexes
-- ---------------------------------------------------------------------------

-- Prevent duplicate external_id per tournament/org combination
CREATE UNIQUE INDEX IF NOT EXISTS idx_tournament_external_participants_unique_external_id
    ON public.tournament_external_participants (tournament_id, organization_id, external_id);

-- Participant listing per tournament/org
CREATE INDEX IF NOT EXISTS idx_tournament_external_participants_tournament_org
    ON public.tournament_external_participants (tournament_id, organization_id);

-- ---------------------------------------------------------------------------
-- 4. Row-Level Security
-- ---------------------------------------------------------------------------
ALTER TABLE public.tournament_external_participants ENABLE ROW LEVEL SECURITY;

-- Service_role only: all API key requests run as service_role.
-- No authenticated (JWT) SELECT policy — this is an API-key-only surface.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'tournament_external_participants'
          AND policyname = 'tournament_external_participants_service_all'
    ) THEN
        CREATE POLICY tournament_external_participants_service_all
            ON public.tournament_external_participants
            FOR ALL
            TO service_role
            USING (true)
            WITH CHECK (true);
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- 5. GRANTs
-- ---------------------------------------------------------------------------
GRANT ALL ON public.tournament_external_participants TO service_role;
