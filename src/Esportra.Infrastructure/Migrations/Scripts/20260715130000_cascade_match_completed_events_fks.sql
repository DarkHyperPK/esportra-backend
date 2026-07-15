-- ============================================================================
-- Migration: 20260715130000_cascade_match_completed_events_fks.sql
-- Purpose:   Fix FK constraints on match_completed_events that block
--            tournament deletion when MockTeamCleanup removes mock teams.
--            - winner_id, loser_id → teams(id) ON DELETE SET NULL
--            - match_id → brkt_matches(id) ON DELETE CASCADE
-- ============================================================================

-- match_completed_events.winner_id -> teams(id) ON DELETE SET NULL
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'match_completed_events_winner_id_fkey'
          AND table_name = 'match_completed_events'
    ) THEN
        ALTER TABLE match_completed_events DROP CONSTRAINT match_completed_events_winner_id_fkey;
    END IF;

    ALTER TABLE match_completed_events
        ADD CONSTRAINT match_completed_events_winner_id_fkey
        FOREIGN KEY (winner_id) REFERENCES teams(id) ON DELETE SET NULL;
END $$;

-- match_completed_events.loser_id -> teams(id) ON DELETE SET NULL
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'match_completed_events_loser_id_fkey'
          AND table_name = 'match_completed_events'
    ) THEN
        ALTER TABLE match_completed_events DROP CONSTRAINT match_completed_events_loser_id_fkey;
    END IF;

    ALTER TABLE match_completed_events
        ADD CONSTRAINT match_completed_events_loser_id_fkey
        FOREIGN KEY (loser_id) REFERENCES teams(id) ON DELETE SET NULL;
END $$;

-- match_completed_events.match_id -> brkt_matches(id) ON DELETE CASCADE
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'match_completed_events_match_id_fkey'
          AND table_name = 'match_completed_events'
    ) THEN
        ALTER TABLE match_completed_events DROP CONSTRAINT match_completed_events_match_id_fkey;
    END IF;

    ALTER TABLE match_completed_events
        ADD CONSTRAINT match_completed_events_match_id_fkey
        FOREIGN KEY (match_id) REFERENCES brkt_matches(id) ON DELETE CASCADE;
END $$;
