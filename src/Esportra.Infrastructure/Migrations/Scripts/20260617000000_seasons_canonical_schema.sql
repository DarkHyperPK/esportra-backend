-- Canonical season schema.
-- Forward-only and safe for production: no drops, no resets, no legacy season tables.

CREATE TABLE IF NOT EXISTS public.seasons (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name TEXT NOT NULL,
    slug TEXT NOT NULL,
    game TEXT NOT NULL,
    description TEXT,
    participant_mode TEXT NOT NULL DEFAULT 'team',
    status TEXT NOT NULL DEFAULT 'draft',
    owner_user_id UUID NOT NULL REFERENCES public.profiles(id) ON DELETE RESTRICT,
    organization_id UUID REFERENCES public.organizations(id) ON DELETE SET NULL,
    is_public BOOLEAN NOT NULL DEFAULT TRUE,
    allow_manual_overrides BOOLEAN NOT NULL DEFAULT FALSE,
    start_date TIMESTAMPTZ,
    end_date TIMESTAMPTZ,
    banner_url TEXT,
    logo_url TEXT,
    settings JSONB NOT NULL DEFAULT '{}'::jsonb,
    last_standings_sync_at TIMESTAMPTZ,
    version INT NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at TIMESTAMPTZ,
    CONSTRAINT chk_seasons_participant_mode CHECK (participant_mode IN ('team','solo')),
    CONSTRAINT chk_seasons_status CHECK (status IN ('draft','published','active','completed','archived','cancelled')),
    CONSTRAINT chk_seasons_date_order CHECK (end_date IS NULL OR start_date IS NULL OR end_date >= start_date)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_seasons_slug_active ON public.seasons(LOWER(slug)) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_seasons_owner_user_id ON public.seasons(owner_user_id);
CREATE INDEX IF NOT EXISTS idx_seasons_organization_id ON public.seasons(organization_id);
CREATE INDEX IF NOT EXISTS idx_seasons_status_public ON public.seasons(status, is_public) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_seasons_created_at ON public.seasons(created_at DESC) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_seasons_game ON public.seasons(game) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_seasons_last_standings_sync ON public.seasons(last_standings_sync_at) WHERE deleted_at IS NULL;

CREATE TABLE IF NOT EXISTS public.season_nodes (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id UUID NOT NULL REFERENCES public.seasons(id) ON DELETE CASCADE,
    parent_node_id UUID REFERENCES public.season_nodes(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    slug TEXT,
    node_type TEXT NOT NULL,
    display_order INT NOT NULL DEFAULT 0,
    region TEXT,
    city TEXT,
    country TEXT,
    linked_tournament_id UUID REFERENCES public.tournaments(id) ON DELETE SET NULL,
    linked_stage_id UUID REFERENCES public.tournament_stages(id) ON DELETE SET NULL,
    status TEXT NOT NULL DEFAULT 'draft',
    registration_deadline TIMESTAMPTZ,
    starts_at TIMESTAMPTZ,
    ends_at TIMESTAMPTZ,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_season_nodes_node_type CHECK (node_type IN ('root','qualifier','event','stage','final','custom')),
    CONSTRAINT chk_season_nodes_status CHECK (status IN ('draft','scheduled','live','completed','archived')),
    CONSTRAINT chk_season_nodes_not_self_parent CHECK (parent_node_id IS NULL OR parent_node_id <> id),
    CONSTRAINT chk_season_nodes_date_order CHECK (ends_at IS NULL OR starts_at IS NULL OR ends_at >= starts_at)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_season_nodes_root ON public.season_nodes(season_id) WHERE node_type = 'root';
CREATE INDEX IF NOT EXISTS idx_season_nodes_season_id ON public.season_nodes(season_id);
CREATE INDEX IF NOT EXISTS idx_season_nodes_parent_node_id ON public.season_nodes(parent_node_id);
CREATE INDEX IF NOT EXISTS idx_season_nodes_linked_tournament_id ON public.season_nodes(linked_tournament_id);
CREATE INDEX IF NOT EXISTS idx_season_nodes_status ON public.season_nodes(status);

CREATE TABLE IF NOT EXISTS public.season_tournaments (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id UUID NOT NULL REFERENCES public.seasons(id) ON DELETE CASCADE,
    tournament_id UUID NOT NULL REFERENCES public.tournaments(id) ON DELETE CASCADE,
    season_role TEXT NOT NULL DEFAULT 'event',
    season_stage_order INT NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_season_tournaments_season_tournament UNIQUE (season_id, tournament_id),
    CONSTRAINT chk_season_tournaments_role CHECK (season_role IN ('qualifier','event','finals','custom'))
);

CREATE INDEX IF NOT EXISTS idx_season_tournaments_season_id ON public.season_tournaments(season_id);
CREATE INDEX IF NOT EXISTS idx_season_tournaments_tournament_id ON public.season_tournaments(tournament_id);
CREATE INDEX IF NOT EXISTS idx_season_tournaments_tournament_season ON public.season_tournaments(tournament_id, season_id);

CREATE TABLE IF NOT EXISTS public.season_participants (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id UUID NOT NULL REFERENCES public.seasons(id) ON DELETE CASCADE,
    team_id UUID NOT NULL REFERENCES public.teams(id) ON DELETE CASCADE,
    team_name TEXT NOT NULL,
    team_logo_url TEXT,
    team_slug TEXT,
    status TEXT NOT NULL DEFAULT 'pending',
    registered_by UUID REFERENCES public.profiles(id) ON DELETE SET NULL,
    notes TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_season_participants_season_team UNIQUE (season_id, team_id),
    CONSTRAINT chk_season_participants_status CHECK (status IN ('pending','approved','rejected'))
);

CREATE INDEX IF NOT EXISTS idx_season_participants_season_id ON public.season_participants(season_id);
CREATE INDEX IF NOT EXISTS idx_season_participants_team_id ON public.season_participants(team_id);
CREATE INDEX IF NOT EXISTS idx_season_participants_status ON public.season_participants(status);

CREATE TABLE IF NOT EXISTS public.season_standings (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id UUID NOT NULL REFERENCES public.seasons(id) ON DELETE CASCADE,
    team_id UUID NOT NULL REFERENCES public.teams(id) ON DELETE CASCADE,
    total_points INT NOT NULL DEFAULT 0,
    qualification_status TEXT,
    standing_rank INT,
    version INT NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_season_standings_season_team UNIQUE (season_id, team_id),
    CONSTRAINT chk_season_standings_qualification_status CHECK (qualification_status IN ('qualified','eliminated','pending'))
);

CREATE INDEX IF NOT EXISTS idx_season_standings_team_id ON public.season_standings(team_id);
CREATE INDEX IF NOT EXISTS idx_season_standings_season_standing_rank ON public.season_standings(season_id, standing_rank);
CREATE INDEX IF NOT EXISTS idx_season_standings_season_standing_rank_cover ON public.season_standings(season_id, standing_rank, total_points, team_id, qualification_status);

CREATE TABLE IF NOT EXISTS public.season_qualification_records (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id UUID NOT NULL REFERENCES public.seasons(id) ON DELETE CASCADE,
    source_node_id UUID REFERENCES public.season_nodes(id) ON DELETE SET NULL,
    destination_node_id UUID REFERENCES public.season_nodes(id) ON DELETE SET NULL,
    team_id UUID REFERENCES public.teams(id) ON DELETE CASCADE,
    user_id UUID REFERENCES public.profiles(id) ON DELETE SET NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    qualification_type TEXT,
    display_name TEXT,
    notes TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_season_qualification_subject CHECK (team_id IS NOT NULL OR user_id IS NOT NULL),
    CONSTRAINT chk_season_qualification_status CHECK (status IN ('qualified','eliminated','pending')),
    CONSTRAINT chk_season_qualification_type CHECK (qualification_type IN ('qualified','wildcard','reserve'))
);

CREATE INDEX IF NOT EXISTS idx_season_qualification_records_season_id ON public.season_qualification_records(season_id);
CREATE INDEX IF NOT EXISTS idx_season_qualification_records_destination_node_id ON public.season_qualification_records(destination_node_id);
CREATE INDEX IF NOT EXISTS idx_season_qualification_records_team_id ON public.season_qualification_records(team_id);
CREATE INDEX IF NOT EXISTS idx_season_qualification_records_team_season ON public.season_qualification_records(team_id, season_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_qualification_records_team_destination ON public.season_qualification_records(season_id, team_id, destination_node_id);

CREATE TABLE IF NOT EXISTS public.season_point_rules (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id UUID NOT NULL REFERENCES public.seasons(id) ON DELETE CASCADE,
    tournament_id UUID REFERENCES public.tournaments(id) ON DELETE CASCADE,
    source_node_id UUID REFERENCES public.season_nodes(id) ON DELETE CASCADE,
    source_stage_id UUID REFERENCES public.tournament_stages(id) ON DELETE SET NULL,
    destination_node_id UUID REFERENCES public.season_nodes(id) ON DELETE SET NULL,
    placement_start INT NOT NULL,
    placement_end INT NOT NULL,
    points INT NOT NULL DEFAULT 0,
    qualification_status TEXT,
    destination_tournament_id UUID REFERENCES public.tournaments(id) ON DELETE SET NULL,
    auto_create_qualification BOOLEAN NOT NULL DEFAULT FALSE,
    region_key TEXT,
    version INT NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_season_point_rules_placement_start CHECK (placement_start > 0),
    CONSTRAINT chk_season_point_rules_placement_order CHECK (placement_end >= placement_start),
    CONSTRAINT chk_season_point_rules_qualification_status CHECK (qualification_status IN ('qualified','eliminated','pending'))
);

CREATE INDEX IF NOT EXISTS idx_season_point_rules_season_id ON public.season_point_rules(season_id);
CREATE INDEX IF NOT EXISTS idx_season_point_rules_tournament_id ON public.season_point_rules(tournament_id);
CREATE INDEX IF NOT EXISTS idx_season_point_rules_source_node_id ON public.season_point_rules(source_node_id);
CREATE INDEX IF NOT EXISTS idx_season_point_rules_placement ON public.season_point_rules(season_id, placement_start ASC, placement_end ASC);

CREATE TABLE IF NOT EXISTS public.season_advancement_rules (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id UUID NOT NULL REFERENCES public.seasons(id) ON DELETE CASCADE,
    source_tournament_id UUID REFERENCES public.tournaments(id) ON DELETE CASCADE,
    target_tournament_id UUID REFERENCES public.tournaments(id) ON DELETE SET NULL,
    source_node_id UUID REFERENCES public.season_nodes(id) ON DELETE CASCADE,
    target_node_id UUID REFERENCES public.season_nodes(id) ON DELETE SET NULL,
    placement_start INT NOT NULL,
    placement_end INT NOT NULL,
    advancement_count INT NOT NULL,
    seed_mode TEXT,
    version INT NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_season_advancement_rules_placement_start CHECK (placement_start > 0),
    CONSTRAINT chk_season_advancement_rules_placement_order CHECK (placement_end >= placement_start),
    CONSTRAINT chk_season_advancement_rules_count CHECK (advancement_count > 0),
    CONSTRAINT chk_season_advancement_rules_seed_mode CHECK (seed_mode IN ('random','manual','top_seeded'))
);

CREATE INDEX IF NOT EXISTS idx_season_advancement_rules_season_id ON public.season_advancement_rules(season_id);
CREATE INDEX IF NOT EXISTS idx_season_advancement_rules_source_tournament_id ON public.season_advancement_rules(source_tournament_id);
CREATE INDEX IF NOT EXISTS idx_season_advancement_rules_source_node_id ON public.season_advancement_rules(source_node_id);

CREATE TABLE IF NOT EXISTS public.season_staff (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id UUID NOT NULL REFERENCES public.seasons(id) ON DELETE CASCADE,
    user_id UUID NOT NULL REFERENCES public.profiles(id) ON DELETE CASCADE,
    role TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_season_staff_season_user UNIQUE (season_id, user_id),
    CONSTRAINT chk_season_staff_role CHECK (role IN ('co_organizer','admin'))
);

CREATE INDEX IF NOT EXISTS idx_season_staff_season_id ON public.season_staff(season_id);
CREATE INDEX IF NOT EXISTS idx_season_staff_user_id ON public.season_staff(user_id);

CREATE TABLE IF NOT EXISTS public.season_announcements (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    season_id UUID NOT NULL REFERENCES public.seasons(id) ON DELETE CASCADE,
    title TEXT NOT NULL,
    content TEXT,
    is_public BOOLEAN NOT NULL DEFAULT TRUE,
    published_at TIMESTAMPTZ,
    created_by UUID REFERENCES public.profiles(id) ON DELETE SET NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_season_announcements_season_id ON public.season_announcements(season_id);
CREATE INDEX IF NOT EXISTS idx_season_announcements_public ON public.season_announcements(season_id, is_public, published_at);
CREATE INDEX IF NOT EXISTS idx_audit_logs_season ON public.audit_logs(target_type, target_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_tournament_participants_tournament_team ON public.tournament_participants(tournament_id, team_id) WHERE status NOT IN ('rejected', 'cancelled');
CREATE INDEX IF NOT EXISTS idx_brkt_matches_version_status ON public.brkt_matches(version_id, status) WHERE team1_id IS NOT NULL;
