-- Enterprise game catalog runtime projection.
-- Additive and forward-only: existing tournament/catalog behavior remains compatible.

CREATE TABLE IF NOT EXISTS public.game_catalog_versions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    catalog_version TEXT NOT NULL,
    schema_version INT NOT NULL,
    content_hash TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    is_active BOOLEAN NOT NULL DEFAULT FALSE,
    error_message TEXT,
    imported_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_game_catalog_versions_status CHECK (status IN ('pending','active','failed')),
    CONSTRAINT chk_game_catalog_versions_schema_version CHECK (schema_version > 0)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_game_catalog_versions_content_hash
    ON public.game_catalog_versions(content_hash);

CREATE UNIQUE INDEX IF NOT EXISTS ux_game_catalog_versions_active
    ON public.game_catalog_versions(is_active)
    WHERE is_active = TRUE;

CREATE TABLE IF NOT EXISTS public.game_catalog_games (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    version_id UUID NOT NULL REFERENCES public.game_catalog_versions(id) ON DELETE CASCADE,
    slug TEXT NOT NULL,
    name TEXT NOT NULL,
    category TEXT,
    game_type TEXT NOT NULL,
    default_mode_key TEXT NOT NULL,
    features JSONB NOT NULL DEFAULT '{}'::jsonb,
    br_config JSONB,
    raw JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_game_catalog_games_version_slug UNIQUE (version_id, slug),
    CONSTRAINT chk_game_catalog_games_game_type CHECK (game_type IN ('bracket','battle_royale'))
);

CREATE INDEX IF NOT EXISTS idx_game_catalog_games_slug
    ON public.game_catalog_games(slug);

CREATE TABLE IF NOT EXISTS public.game_catalog_game_modes (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    version_id UUID NOT NULL REFERENCES public.game_catalog_versions(id) ON DELETE CASCADE,
    game_slug TEXT NOT NULL,
    mode_key TEXT NOT NULL,
    name TEXT NOT NULL,
    team_size INT NOT NULL,
    participant_mode TEXT NOT NULL,
    allows_substitutes BOOLEAN NOT NULL DEFAULT TRUE,
    max_roster_size INT,
    aliases TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
    raw JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_game_catalog_modes_version_game_mode UNIQUE (version_id, game_slug, mode_key),
    CONSTRAINT chk_game_catalog_modes_team_size CHECK (team_size > 0),
    CONSTRAINT chk_game_catalog_modes_participant_mode CHECK (participant_mode IN ('team','solo')),
    CONSTRAINT chk_game_catalog_modes_roster_size CHECK (max_roster_size IS NULL OR max_roster_size >= team_size)
);

CREATE INDEX IF NOT EXISTS idx_game_catalog_modes_game_slug
    ON public.game_catalog_game_modes(game_slug);

CREATE TABLE IF NOT EXISTS public.game_catalog_tournament_structures (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    version_id UUID NOT NULL REFERENCES public.game_catalog_versions(id) ON DELETE CASCADE,
    game_slug TEXT NOT NULL,
    structure_key TEXT NOT NULL,
    name TEXT NOT NULL,
    is_default BOOLEAN NOT NULL DEFAULT FALSE,
    raw JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_game_catalog_structures_version_game_structure UNIQUE (version_id, game_slug, structure_key),
    CONSTRAINT chk_game_catalog_structures_key CHECK (structure_key IN ('single_elimination','double_elimination','swiss','round_robin','battle_royale'))
);

CREATE INDEX IF NOT EXISTS idx_game_catalog_structures_game_slug
    ON public.game_catalog_tournament_structures(game_slug);

CREATE TABLE IF NOT EXISTS public.game_catalog_game_aliases (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    version_id UUID NOT NULL REFERENCES public.game_catalog_versions(id) ON DELETE CASCADE,
    game_slug TEXT NOT NULL,
    alias TEXT NOT NULL,
    alias_type TEXT NOT NULL DEFAULT 'legacy',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_game_catalog_aliases_type CHECK (alias_type IN ('slug','name','legacy'))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_game_catalog_aliases_version_alias
    ON public.game_catalog_game_aliases(version_id, LOWER(alias));

CREATE INDEX IF NOT EXISTS idx_game_catalog_aliases_game_slug
    ON public.game_catalog_game_aliases(game_slug);

ALTER TABLE public.tournaments
    ADD COLUMN IF NOT EXISTS game_mode TEXT;

CREATE INDEX IF NOT EXISTS idx_tournaments_game_mode
    ON public.tournaments(game_mode);

DO $$
DECLARE
    table_name TEXT;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'game_catalog_versions',
        'game_catalog_games',
        'game_catalog_game_modes',
        'game_catalog_tournament_structures',
        'game_catalog_game_aliases'
    ]
    LOOP
        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', table_name);
        EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON public.%I TO service_role', table_name);
        EXECUTE format('REVOKE INSERT, UPDATE, DELETE ON public.%I FROM authenticated', table_name);
        EXECUTE format('REVOKE INSERT, UPDATE, DELETE ON public.%I FROM anon', table_name);
        EXECUTE format('GRANT SELECT ON public.%I TO authenticated', table_name);
        EXECUTE format('GRANT SELECT ON public.%I TO anon', table_name);
        EXECUTE format('DROP POLICY IF EXISTS %I ON public.%I', table_name || '_service_role_all', table_name);
        EXECUTE format('CREATE POLICY %I ON public.%I FOR ALL TO service_role USING (true) WITH CHECK (true)', table_name || '_service_role_all', table_name);
        EXECUTE format('DROP POLICY IF EXISTS %I ON public.%I', table_name || '_public_read', table_name);
        EXECUTE format('CREATE POLICY %I ON public.%I FOR SELECT TO authenticated, anon USING (true)', table_name || '_public_read', table_name);
    END LOOP;
END $$;
