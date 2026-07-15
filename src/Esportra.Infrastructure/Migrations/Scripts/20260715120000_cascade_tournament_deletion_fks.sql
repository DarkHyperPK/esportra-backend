-- ============================================================================
-- Migration: 20260715120000_cascade_tournament_deletion_fks.sql
-- Purpose:   Change ON DELETE RESTRICT/NO ACTION to ON DELETE CASCADE on
--            pre-baseline child tables of tournaments so that permanent
--            deletion no longer triggers FK violations (error 23503).
-- ============================================================================

-- ── Tier 1: Direct children of tournaments ─────────────────────────────────

-- tournament_participants.tournament_id -> tournaments(id)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'tournament_participants_tournament_id_fkey'
          AND table_name = 'tournament_participants'
    ) THEN
        ALTER TABLE tournament_participants DROP CONSTRAINT tournament_participants_tournament_id_fkey;
    END IF;

    ALTER TABLE tournament_participants
        ADD CONSTRAINT tournament_participants_tournament_id_fkey
        FOREIGN KEY (tournament_id) REFERENCES tournaments(id) ON DELETE CASCADE;
END $$;

-- tournament_stages.tournament_id -> tournaments(id)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'tournament_stages_tournament_id_fkey'
          AND table_name = 'tournament_stages'
    ) THEN
        ALTER TABLE tournament_stages DROP CONSTRAINT tournament_stages_tournament_id_fkey;
    END IF;

    ALTER TABLE tournament_stages
        ADD CONSTRAINT tournament_stages_tournament_id_fkey
        FOREIGN KEY (tournament_id) REFERENCES tournaments(id) ON DELETE CASCADE;
END $$;

-- brkt_versions.tournament_id -> tournaments(id)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'brkt_versions_tournament_id_fkey'
          AND table_name = 'brkt_versions'
    ) THEN
        ALTER TABLE brkt_versions DROP CONSTRAINT brkt_versions_tournament_id_fkey;
    END IF;

    ALTER TABLE brkt_versions
        ADD CONSTRAINT brkt_versions_tournament_id_fkey
        FOREIGN KEY (tournament_id) REFERENCES tournaments(id) ON DELETE CASCADE;
END $$;

-- tournament_disputes.tournament_id -> tournaments(id)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'tournament_disputes_tournament_id_fkey'
          AND table_name = 'tournament_disputes'
    ) THEN
        ALTER TABLE tournament_disputes DROP CONSTRAINT tournament_disputes_tournament_id_fkey;
    END IF;

    ALTER TABLE tournament_disputes
        ADD CONSTRAINT tournament_disputes_tournament_id_fkey
        FOREIGN KEY (tournament_id) REFERENCES tournaments(id) ON DELETE CASCADE;
END $$;

-- staff_tournament_assignments.tournament_id -> tournaments(id)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'staff_tournament_assignments_tournament_id_fkey'
          AND table_name = 'staff_tournament_assignments'
    ) THEN
        ALTER TABLE staff_tournament_assignments DROP CONSTRAINT staff_tournament_assignments_tournament_id_fkey;
    END IF;

    ALTER TABLE staff_tournament_assignments
        ADD CONSTRAINT staff_tournament_assignments_tournament_id_fkey
        FOREIGN KEY (tournament_id) REFERENCES tournaments(id) ON DELETE CASCADE;
END $$;

-- ── Tier 2: Children of brkt_versions ──────────────────────────────────────

-- brkt_matches.version_id -> brkt_versions(id)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'brkt_matches_version_id_fkey'
          AND table_name = 'brkt_matches'
    ) THEN
        ALTER TABLE brkt_matches DROP CONSTRAINT brkt_matches_version_id_fkey;
    END IF;

    ALTER TABLE brkt_matches
        ADD CONSTRAINT brkt_matches_version_id_fkey
        FOREIGN KEY (version_id) REFERENCES brkt_versions(id) ON DELETE CASCADE;
END $$;

-- brkt_layout.version_id -> brkt_versions(id)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'brkt_layout_version_id_fkey'
          AND table_name = 'brkt_layout'
    ) THEN
        ALTER TABLE brkt_layout DROP CONSTRAINT brkt_layout_version_id_fkey;
    END IF;

    ALTER TABLE brkt_layout
        ADD CONSTRAINT brkt_layout_version_id_fkey
        FOREIGN KEY (version_id) REFERENCES brkt_versions(id) ON DELETE CASCADE;
END $$;

-- brkt_advancements.version_id -> brkt_versions(id)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'brkt_advancements_version_id_fkey'
          AND table_name = 'brkt_advancements'
    ) THEN
        ALTER TABLE brkt_advancements DROP CONSTRAINT brkt_advancements_version_id_fkey;
    END IF;

    ALTER TABLE brkt_advancements
        ADD CONSTRAINT brkt_advancements_version_id_fkey
        FOREIGN KEY (version_id) REFERENCES brkt_versions(id) ON DELETE CASCADE;
END $$;

-- ── Tier 3: Children of brkt_matches ───────────────────────────────────────

-- brkt_match_games.match_id -> brkt_matches(id)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'brkt_match_games_match_id_fkey'
          AND table_name = 'brkt_match_games'
    ) THEN
        ALTER TABLE brkt_match_games DROP CONSTRAINT brkt_match_games_match_id_fkey;
    END IF;

    ALTER TABLE brkt_match_games
        ADD CONSTRAINT brkt_match_games_match_id_fkey
        FOREIGN KEY (match_id) REFERENCES brkt_matches(id) ON DELETE CASCADE;
END $$;

-- brkt_match_events.match_id -> brkt_matches(id)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'brkt_match_events_match_id_fkey'
          AND table_name = 'brkt_match_events'
    ) THEN
        ALTER TABLE brkt_match_events DROP CONSTRAINT brkt_match_events_match_id_fkey;
    END IF;

    ALTER TABLE brkt_match_events
        ADD CONSTRAINT brkt_match_events_match_id_fkey
        FOREIGN KEY (match_id) REFERENCES brkt_matches(id) ON DELETE CASCADE;
END $$;

-- match_result_reports.match_id -> brkt_matches(id)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'match_result_reports_match_id_fkey'
          AND table_name = 'match_result_reports'
    ) THEN
        ALTER TABLE match_result_reports DROP CONSTRAINT match_result_reports_match_id_fkey;
    END IF;

    ALTER TABLE match_result_reports
        ADD CONSTRAINT match_result_reports_match_id_fkey
        FOREIGN KEY (match_id) REFERENCES brkt_matches(id) ON DELETE CASCADE;
END $$;

-- match_map_vetos.match_id -> brkt_matches(id)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'match_map_vetos_match_id_fkey'
          AND table_name = 'match_map_vetos'
    ) THEN
        ALTER TABLE match_map_vetos DROP CONSTRAINT match_map_vetos_match_id_fkey;
    END IF;

    ALTER TABLE match_map_vetos
        ADD CONSTRAINT match_map_vetos_match_id_fkey
        FOREIGN KEY (match_id) REFERENCES brkt_matches(id) ON DELETE CASCADE;
END $$;

-- tournament_disputes.match_id -> brkt_matches(id)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'tournament_disputes_match_id_fkey'
          AND table_name = 'tournament_disputes'
    ) THEN
        ALTER TABLE tournament_disputes DROP CONSTRAINT tournament_disputes_match_id_fkey;
    END IF;

    ALTER TABLE tournament_disputes
        ADD CONSTRAINT tournament_disputes_match_id_fkey
        FOREIGN KEY (match_id) REFERENCES brkt_matches(id) ON DELETE CASCADE;
END $$;

-- ── Tier 4: Children of match_map_vetos ────────────────────────────────────

-- match_map_veto_actions.veto_id -> match_map_vetos(id)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'match_map_veto_actions_veto_id_fkey'
          AND table_name = 'match_map_veto_actions'
    ) THEN
        ALTER TABLE match_map_veto_actions DROP CONSTRAINT match_map_veto_actions_veto_id_fkey;
    END IF;

    ALTER TABLE match_map_veto_actions
        ADD CONSTRAINT match_map_veto_actions_veto_id_fkey
        FOREIGN KEY (veto_id) REFERENCES match_map_vetos(id) ON DELETE CASCADE;
END $$;

-- ── Tier 5: Children of tournament_disputes ────────────────────────────────

-- dispute_comments.dispute_id -> tournament_disputes(id)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'dispute_comments_dispute_id_fkey'
          AND table_name = 'dispute_comments'
    ) THEN
        ALTER TABLE dispute_comments DROP CONSTRAINT dispute_comments_dispute_id_fkey;
    END IF;

    ALTER TABLE dispute_comments
        ADD CONSTRAINT dispute_comments_dispute_id_fkey
        FOREIGN KEY (dispute_id) REFERENCES tournament_disputes(id) ON DELETE CASCADE;
END $$;
