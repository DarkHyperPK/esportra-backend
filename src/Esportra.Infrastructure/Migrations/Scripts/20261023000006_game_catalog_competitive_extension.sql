-- Extend the game catalog with competitive metadata tables:
-- roles, rank tiers, and matchmaking regions.
-- All three tables are scoped to a catalog version + game_slug pair so they
-- travel with catalog versioning and can be swapped atomically.

-- ── game_catalog_game_roles ───────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.game_catalog_game_roles (
    id           UUID    PRIMARY KEY DEFAULT gen_random_uuid(),
    version_id   UUID    NOT NULL REFERENCES public.game_catalog_versions(id) ON DELETE CASCADE,
    game_slug    TEXT    NOT NULL,
    role_key     TEXT    NOT NULL,
    display_name TEXT    NOT NULL,
    description  TEXT,
    sort_order   INT     NOT NULL DEFAULT 0,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'game_catalog_game_roles_version_slug_role_key'
  ) THEN
    ALTER TABLE public.game_catalog_game_roles
      ADD CONSTRAINT game_catalog_game_roles_version_slug_role_key
      UNIQUE (version_id, game_slug, role_key);
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_game_catalog_game_roles_slug
    ON public.game_catalog_game_roles(game_slug);

-- ── game_catalog_rank_tiers ───────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.game_catalog_rank_tiers (
    id             UUID    PRIMARY KEY DEFAULT gen_random_uuid(),
    version_id     UUID    NOT NULL REFERENCES public.game_catalog_versions(id) ON DELETE CASCADE,
    game_slug      TEXT    NOT NULL,
    tier_key       TEXT    NOT NULL,
    display_name   TEXT    NOT NULL,
    sort_order     INT     NOT NULL,
    division_count INT     NOT NULL DEFAULT 1,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'game_catalog_rank_tiers_version_slug_tier_key'
  ) THEN
    ALTER TABLE public.game_catalog_rank_tiers
      ADD CONSTRAINT game_catalog_rank_tiers_version_slug_tier_key
      UNIQUE (version_id, game_slug, tier_key);
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_game_catalog_rank_tiers_slug
    ON public.game_catalog_rank_tiers(game_slug);

-- ── game_catalog_regions ──────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.game_catalog_regions (
    id           UUID    PRIMARY KEY DEFAULT gen_random_uuid(),
    version_id   UUID    NOT NULL REFERENCES public.game_catalog_versions(id) ON DELETE CASCADE,
    game_slug    TEXT    NOT NULL,
    region_key   TEXT    NOT NULL,
    display_name TEXT    NOT NULL,
    server_ids   TEXT[]  NOT NULL DEFAULT ARRAY[]::TEXT[],
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'game_catalog_regions_version_slug_region_key'
  ) THEN
    ALTER TABLE public.game_catalog_regions
      ADD CONSTRAINT game_catalog_regions_version_slug_region_key
      UNIQUE (version_id, game_slug, region_key);
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_game_catalog_regions_slug
    ON public.game_catalog_regions(game_slug);

-- ── RLS: mirror the pattern from game_catalog_runtime.sql ────────────────────
-- service_role: full access; authenticated + anon: read-only.

DO $$
DECLARE
    tbl TEXT;
BEGIN
    FOREACH tbl IN ARRAY ARRAY[
        'game_catalog_game_roles',
        'game_catalog_rank_tiers',
        'game_catalog_regions'
    ]
    LOOP
        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', tbl);
        EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON public.%I TO service_role', tbl);
        EXECUTE format('REVOKE INSERT, UPDATE, DELETE ON public.%I FROM authenticated', tbl);
        EXECUTE format('REVOKE INSERT, UPDATE, DELETE ON public.%I FROM anon', tbl);
        EXECUTE format('GRANT SELECT ON public.%I TO authenticated', tbl);
        EXECUTE format('GRANT SELECT ON public.%I TO anon', tbl);
        EXECUTE format('DROP POLICY IF EXISTS %I ON public.%I', tbl || '_service_role_all', tbl);
        EXECUTE format('CREATE POLICY %I ON public.%I FOR ALL TO service_role USING (true) WITH CHECK (true)', tbl || '_service_role_all', tbl);
        EXECUTE format('DROP POLICY IF EXISTS %I ON public.%I', tbl || '_public_read', tbl);
        EXECUTE format('CREATE POLICY %I ON public.%I FOR SELECT TO authenticated, anon USING (true)', tbl || '_public_read', tbl);
    END LOOP;
END $$;

-- ── Seed: Valorant roles, rank tiers, regions ─────────────────────────────────
-- The active catalog version is the single row where is_active = TRUE.
-- The unique partial index ux_game_catalog_versions_active guarantees at most one.
-- If no active version exists yet the DO block exits silently; seed runs on first activation.

DO $$
DECLARE
    v_version_id UUID;
BEGIN
    SELECT id INTO v_version_id
    FROM public.game_catalog_versions
    WHERE is_active = TRUE
    LIMIT 1;

    IF v_version_id IS NULL THEN
        RAISE NOTICE 'game_catalog_competitive_extension: no active catalog version found; seed skipped';
        RETURN;
    END IF;

    -- Roles
    INSERT INTO public.game_catalog_game_roles (version_id, game_slug, role_key, display_name, description, sort_order)
    VALUES
        (v_version_id, 'valorant', 'duelist',    'Duelist',    'Entry fraggers and duelists',    0),
        (v_version_id, 'valorant', 'controller', 'Controller', 'Smoke and area-denial agents',   1),
        (v_version_id, 'valorant', 'initiator',  'Initiator',  'Recon and flank support',         2),
        (v_version_id, 'valorant', 'sentinel',   'Sentinel',   'Anchor and defensive agents',    3),
        (v_version_id, 'valorant', 'flex',        'Flex',        'Adaptable role',                 4)
    ON CONFLICT DO NOTHING;

    -- Rank tiers (25 entries, sort_order 0-24)
    INSERT INTO public.game_catalog_rank_tiers (version_id, game_slug, tier_key, display_name, sort_order, division_count)
    VALUES
        (v_version_id, 'valorant', 'iron1',       'Iron I',        0,  3),
        (v_version_id, 'valorant', 'iron2',       'Iron II',       1,  3),
        (v_version_id, 'valorant', 'iron3',       'Iron III',      2,  3),
        (v_version_id, 'valorant', 'bronze1',     'Bronze I',      3,  3),
        (v_version_id, 'valorant', 'bronze2',     'Bronze II',     4,  3),
        (v_version_id, 'valorant', 'bronze3',     'Bronze III',    5,  3),
        (v_version_id, 'valorant', 'silver1',     'Silver I',      6,  3),
        (v_version_id, 'valorant', 'silver2',     'Silver II',     7,  3),
        (v_version_id, 'valorant', 'silver3',     'Silver III',    8,  3),
        (v_version_id, 'valorant', 'gold1',       'Gold I',        9,  3),
        (v_version_id, 'valorant', 'gold2',       'Gold II',       10, 3),
        (v_version_id, 'valorant', 'gold3',       'Gold III',      11, 3),
        (v_version_id, 'valorant', 'platinum1',   'Platinum I',    12, 3),
        (v_version_id, 'valorant', 'platinum2',   'Platinum II',   13, 3),
        (v_version_id, 'valorant', 'platinum3',   'Platinum III',  14, 3),
        (v_version_id, 'valorant', 'diamond1',    'Diamond I',     15, 3),
        (v_version_id, 'valorant', 'diamond2',    'Diamond II',    16, 3),
        (v_version_id, 'valorant', 'diamond3',    'Diamond III',   17, 3),
        (v_version_id, 'valorant', 'ascendant1',  'Ascendant I',   18, 3),
        (v_version_id, 'valorant', 'ascendant2',  'Ascendant II',  19, 3),
        (v_version_id, 'valorant', 'ascendant3',  'Ascendant III', 20, 3),
        (v_version_id, 'valorant', 'immortal1',   'Immortal I',    21, 3),
        (v_version_id, 'valorant', 'immortal2',   'Immortal II',   22, 3),
        (v_version_id, 'valorant', 'immortal3',   'Immortal III',  23, 3),
        (v_version_id, 'valorant', 'radiant',     'Radiant',       24, 1)
    ON CONFLICT DO NOTHING;

    -- Regions
    INSERT INTO public.game_catalog_regions (version_id, game_slug, region_key, display_name, server_ids)
    VALUES
        (v_version_id, 'valorant', 'na',    'NA',    ARRAY['na']),
        (v_version_id, 'valorant', 'eu',    'EU',    ARRAY['eu']),
        (v_version_id, 'valorant', 'ap',    'AP',    ARRAY['ap']),
        (v_version_id, 'valorant', 'kr',    'KR',    ARRAY['kr']),
        (v_version_id, 'valorant', 'latam', 'LATAM', ARRAY['latam'])
    ON CONFLICT DO NOTHING;

END $$;
