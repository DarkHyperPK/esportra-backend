-- ============================================================================
-- Migration: Leaderboard Refresh State (watermark)
-- Purpose:   Single-row state for the event-driven leaderboard refresh job.
--
--            The job fingerprints leaderboard source data (counts + max
--            updated_at across matches / tournaments / placements) and skips
--            the rebuild when the fingerprint is unchanged. This collapses
--            burst triggers (many matches finishing together) into one real
--            rebuild, and makes the hourly self-healing sweep a no-op while
--            data is static.
-- ============================================================================

CREATE TABLE IF NOT EXISTS public.leaderboard_refresh_state (
    id                SMALLINT    PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    last_fingerprint  TEXT,
    last_completed_at TIMESTAMPTZ
);

INSERT INTO public.leaderboard_refresh_state (id)
VALUES (1)
ON CONFLICT DO NOTHING;
