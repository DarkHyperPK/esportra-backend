-- PROJ-065 Competitive Phase 1: LFG listings and interests
-- Three enum types, two tables with RLS.

-- ── Enum types (each guarded against re-creation) ─────────────────────────────

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'lfg_listing_status') THEN
        CREATE TYPE public.lfg_listing_status AS ENUM (
            'draft', 'published', 'paused', 'expired', 'closed', 'removed'
        );
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'lfg_intent') THEN
        CREATE TYPE public.lfg_intent AS ENUM (
            'duo', 'group', 'teammates', 'looking_for_team',
            'looking_for_player', 'competitive_roster', 'casual'
        );
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'lfg_interest_status') THEN
        CREATE TYPE public.lfg_interest_status AS ENUM (
            'expressed', 'withdrawn', 'accepted', 'declined', 'blocked'
        );
    END IF;
END $$;

-- ── lfg_listings ──────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.lfg_listings (
    id                   UUID                    PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id              UUID                    NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    game_slug            TEXT                    NOT NULL,
    intent               lfg_intent              NOT NULL,
    mode_key             TEXT,
    region_key           TEXT,
    preferred_servers    TEXT[]                  NOT NULL DEFAULT ARRAY[]::TEXT[],
    min_rank_sort_order  INT,
    max_rank_sort_order  INT,
    preferred_roles      TEXT[]                  NOT NULL DEFAULT ARRAY[]::TEXT[],
    languages            TEXT[]                  NOT NULL DEFAULT ARRAY[]::TEXT[],
    description          TEXT,
    expires_at           TIMESTAMPTZ             NOT NULL DEFAULT (now() + INTERVAL '7 days'),
    status               lfg_listing_status      NOT NULL DEFAULT 'draft',
    created_at           TIMESTAMPTZ             NOT NULL DEFAULT now(),
    updated_at           TIMESTAMPTZ             NOT NULL DEFAULT now()
);

-- CHECK: rank range must be ordered when both bounds are present (guarded)
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'chk_lfg_rank_range'
    ) THEN
        ALTER TABLE public.lfg_listings
            ADD CONSTRAINT chk_lfg_rank_range
            CHECK (min_rank_sort_order IS NULL OR max_rank_sort_order IS NULL OR min_rank_sort_order <= max_rank_sort_order);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_lfg_user
    ON public.lfg_listings (user_id);

-- Partial index for discovery queries (published listings only)
CREATE INDEX IF NOT EXISTS idx_lfg_game_status
    ON public.lfg_listings (game_slug)
    WHERE status = 'published';

CREATE INDEX IF NOT EXISTS idx_lfg_expires
    ON public.lfg_listings (expires_at)
    WHERE status = 'published';

ALTER TABLE public.lfg_listings ENABLE ROW LEVEL SECURITY;

-- Owner: full access to own listings
DROP POLICY IF EXISTS lfg_own ON public.lfg_listings;
CREATE POLICY lfg_own
    ON public.lfg_listings
    FOR ALL
    TO authenticated
    USING  (user_id = auth.uid())
    WITH CHECK (user_id = auth.uid());

-- Public read: authenticated users may browse published listings
DROP POLICY IF EXISTS lfg_public ON public.lfg_listings;
CREATE POLICY lfg_public
    ON public.lfg_listings
    FOR SELECT
    TO authenticated
    USING (status = 'published');

-- Service role: unrestricted
DROP POLICY IF EXISTS lfg_service ON public.lfg_listings;
CREATE POLICY lfg_service
    ON public.lfg_listings
    FOR ALL
    TO service_role
    USING (true)
    WITH CHECK (true);

-- ── lfg_interests ─────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.lfg_interests (
    id           UUID                   PRIMARY KEY DEFAULT gen_random_uuid(),
    listing_id   UUID                   NOT NULL REFERENCES public.lfg_listings(id) ON DELETE CASCADE,
    from_user_id UUID                   NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    to_user_id   UUID                   NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    status       lfg_interest_status    NOT NULL DEFAULT 'expressed',
    message      TEXT,
    created_at   TIMESTAMPTZ            NOT NULL DEFAULT now(),
    updated_at   TIMESTAMPTZ            NOT NULL DEFAULT now()
);

-- Named unique constraint: one expression of interest per user per listing (guarded)
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'lfgi_listing_from_unique'
    ) THEN
        ALTER TABLE public.lfg_interests
            ADD CONSTRAINT lfgi_listing_from_unique UNIQUE (listing_id, from_user_id);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_lfgi_listing
    ON public.lfg_interests (listing_id);

CREATE INDEX IF NOT EXISTS idx_lfgi_from
    ON public.lfg_interests (from_user_id);

CREATE INDEX IF NOT EXISTS idx_lfgi_to
    ON public.lfg_interests (to_user_id);

ALTER TABLE public.lfg_interests ENABLE ROW LEVEL SECURITY;

-- Parties: both the sender and the listing owner may see and manage the interest row.
-- Only the sender may insert (from_user_id = auth.uid() enforced via WITH CHECK).
DROP POLICY IF EXISTS lfgi_parties ON public.lfg_interests;
CREATE POLICY lfgi_parties
    ON public.lfg_interests
    FOR ALL
    TO authenticated
    USING  (from_user_id = auth.uid() OR to_user_id = auth.uid())
    WITH CHECK (from_user_id = auth.uid());

-- Service role: unrestricted
DROP POLICY IF EXISTS lfgi_service ON public.lfg_interests;
CREATE POLICY lfgi_service
    ON public.lfg_interests
    FOR ALL
    TO service_role
    USING (true)
    WITH CHECK (true);
