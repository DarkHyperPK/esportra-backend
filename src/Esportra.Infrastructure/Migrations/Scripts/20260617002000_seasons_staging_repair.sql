-- Staging repair for season schema already created by retired experimental migrations.
-- Safe on production after the canonical migration: repeats missing columns, indexes, grants, and RLS only.

DROP TRIGGER IF EXISTS trg_cleanup_deleted_season ON public.seasons;
DROP FUNCTION IF EXISTS public.cleanup_deleted_season();

ALTER TABLE public.seasons ADD COLUMN IF NOT EXISTS last_standings_sync_at TIMESTAMPTZ;
ALTER TABLE public.seasons ADD COLUMN IF NOT EXISTS version INT NOT NULL DEFAULT 1;
ALTER TABLE public.seasons ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;
ALTER TABLE public.season_point_rules ADD COLUMN IF NOT EXISTS version INT NOT NULL DEFAULT 1;
ALTER TABLE public.season_advancement_rules ADD COLUMN IF NOT EXISTS version INT NOT NULL DEFAULT 1;

CREATE UNIQUE INDEX IF NOT EXISTS ux_seasons_slug_active ON public.seasons(LOWER(slug)) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_seasons_owner_user_id ON public.seasons(owner_user_id);
CREATE INDEX IF NOT EXISTS idx_seasons_organization_id ON public.seasons(organization_id);
CREATE INDEX IF NOT EXISTS idx_seasons_status_public ON public.seasons(status, is_public) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_seasons_created_at ON public.seasons(created_at DESC) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_seasons_game ON public.seasons(game) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_seasons_last_standings_sync ON public.seasons(last_standings_sync_at) WHERE deleted_at IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_season_nodes_root ON public.season_nodes(season_id) WHERE node_type = 'root';
CREATE INDEX IF NOT EXISTS idx_season_nodes_season_id ON public.season_nodes(season_id);
CREATE INDEX IF NOT EXISTS idx_season_nodes_parent_node_id ON public.season_nodes(parent_node_id);
CREATE INDEX IF NOT EXISTS idx_season_nodes_linked_tournament_id ON public.season_nodes(linked_tournament_id);
CREATE INDEX IF NOT EXISTS idx_season_tournaments_tournament_season ON public.season_tournaments(tournament_id, season_id);
CREATE INDEX IF NOT EXISTS idx_season_participants_status ON public.season_participants(status);
CREATE INDEX IF NOT EXISTS idx_season_standings_season_standing_rank_cover ON public.season_standings(season_id, standing_rank, total_points, team_id, qualification_status);
CREATE INDEX IF NOT EXISTS idx_season_point_rules_placement ON public.season_point_rules(season_id, placement_start ASC, placement_end ASC);
CREATE INDEX IF NOT EXISTS idx_season_advancement_rules_source_tournament_id ON public.season_advancement_rules(source_tournament_id);
CREATE INDEX IF NOT EXISTS idx_season_advancement_rules_source_node_id ON public.season_advancement_rules(source_node_id);
CREATE INDEX IF NOT EXISTS idx_audit_logs_season ON public.audit_logs(target_type, target_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_tournament_participants_tournament_team ON public.tournament_participants(tournament_id, team_id) WHERE status NOT IN ('rejected', 'cancelled');
CREATE INDEX IF NOT EXISTS idx_brkt_matches_version_status ON public.brkt_matches(version_id, status) WHERE team1_id IS NOT NULL;

DO $$
DECLARE
    table_name TEXT;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'seasons',
        'season_nodes',
        'season_tournaments',
        'season_participants',
        'season_standings',
        'season_qualification_records',
        'season_point_rules',
        'season_advancement_rules',
        'season_staff',
        'season_announcements'
    ]
    LOOP
        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', table_name);
        EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON public.%I TO service_role', table_name);
        EXECUTE format('REVOKE INSERT, UPDATE, DELETE ON public.%I FROM authenticated', table_name);
        EXECUTE format('GRANT SELECT ON public.%I TO authenticated', table_name);
        EXECUTE format('DROP POLICY IF EXISTS %I ON public.%I', table_name || '_service_role_all', table_name);
        EXECUTE format('CREATE POLICY %I ON public.%I FOR ALL TO service_role USING (true) WITH CHECK (true)', table_name || '_service_role_all', table_name);
    END LOOP;
END;
$$;

DROP POLICY IF EXISTS seasons_authenticated_read ON public.seasons;
CREATE POLICY seasons_authenticated_read ON public.seasons
    FOR SELECT TO authenticated
    USING (
        deleted_at IS NULL
        AND (
            is_public = TRUE
            OR owner_user_id = auth.uid()
            OR EXISTS (
                SELECT 1 FROM public.organizations o
                WHERE o.id = seasons.organization_id AND o.owner_id = auth.uid()
            )
        )
    );

DROP POLICY IF EXISTS season_nodes_authenticated_read ON public.season_nodes;
CREATE POLICY season_nodes_authenticated_read ON public.season_nodes
    FOR SELECT TO authenticated
    USING (EXISTS (SELECT 1 FROM public.seasons s WHERE s.id = season_nodes.season_id));

DROP POLICY IF EXISTS season_tournaments_authenticated_read ON public.season_tournaments;
CREATE POLICY season_tournaments_authenticated_read ON public.season_tournaments
    FOR SELECT TO authenticated
    USING (EXISTS (SELECT 1 FROM public.seasons s WHERE s.id = season_tournaments.season_id));

DROP POLICY IF EXISTS season_participants_authenticated_read ON public.season_participants;
CREATE POLICY season_participants_authenticated_read ON public.season_participants
    FOR SELECT TO authenticated
    USING (EXISTS (SELECT 1 FROM public.seasons s WHERE s.id = season_participants.season_id));

DROP POLICY IF EXISTS season_standings_authenticated_read ON public.season_standings;
CREATE POLICY season_standings_authenticated_read ON public.season_standings
    FOR SELECT TO authenticated
    USING (EXISTS (SELECT 1 FROM public.seasons s WHERE s.id = season_standings.season_id));

DROP POLICY IF EXISTS season_qualification_records_authenticated_read ON public.season_qualification_records;
CREATE POLICY season_qualification_records_authenticated_read ON public.season_qualification_records
    FOR SELECT TO authenticated
    USING (EXISTS (SELECT 1 FROM public.seasons s WHERE s.id = season_qualification_records.season_id));

DROP POLICY IF EXISTS season_point_rules_authenticated_read ON public.season_point_rules;
CREATE POLICY season_point_rules_authenticated_read ON public.season_point_rules
    FOR SELECT TO authenticated
    USING (EXISTS (SELECT 1 FROM public.seasons s WHERE s.id = season_point_rules.season_id));

DROP POLICY IF EXISTS season_advancement_rules_authenticated_read ON public.season_advancement_rules;
CREATE POLICY season_advancement_rules_authenticated_read ON public.season_advancement_rules
    FOR SELECT TO authenticated
    USING (EXISTS (SELECT 1 FROM public.seasons s WHERE s.id = season_advancement_rules.season_id));

DROP POLICY IF EXISTS season_staff_authenticated_read ON public.season_staff;
CREATE POLICY season_staff_authenticated_read ON public.season_staff
    FOR SELECT TO authenticated
    USING (
        user_id = auth.uid()
        OR EXISTS (
            SELECT 1
            FROM public.seasons s
            LEFT JOIN public.organizations o ON o.id = s.organization_id
            WHERE s.id = season_staff.season_id
              AND (s.owner_user_id = auth.uid() OR o.owner_id = auth.uid())
        )
    );

DROP POLICY IF EXISTS season_announcements_authenticated_read ON public.season_announcements;
CREATE POLICY season_announcements_authenticated_read ON public.season_announcements
    FOR SELECT TO authenticated
    USING (
        is_public = TRUE
        OR EXISTS (
            SELECT 1
            FROM public.seasons s
            WHERE s.id = season_announcements.season_id
        )
    );
