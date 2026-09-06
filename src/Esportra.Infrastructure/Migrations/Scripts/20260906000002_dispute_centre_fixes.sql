-- PROJ-007: Dispute Centre Bug Fixes + Enhancements
-- Three targeted schema changes:
--   1. Expand match_result_reports_status_check to include 'rejected'
--   2. Expand tournaments.disputes.evidence bucket limits (50 MB, add PDF)
--   3. Add reopen_count column to tournament_disputes

-- ─────────────────────────────────────────────────────────────────────────────
-- Change 1: match_result_reports_status_check — add 'rejected'
--
-- The organizer dispute-resolution endpoint (TournamentEndpoints.cs) sets
-- status = 'rejected' on losing reports when a dispute is resolved. Without
-- this value in the CHECK constraint, that UPDATE fails at the DB level.
-- Safe approach: drop the old constraint (IF EXISTS supported for DROP), then
-- re-add it with the expanded value set inside a DO $$ guard.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE public.match_result_reports
    DROP CONSTRAINT IF EXISTS match_result_reports_status_check;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'match_result_reports_status_check'
          AND conrelid = 'public.match_result_reports'::regclass
    ) THEN
        ALTER TABLE public.match_result_reports
            ADD CONSTRAINT match_result_reports_status_check
            CHECK (status IN ('pending', 'accepted', 'disputed', 'rejected'));
    END IF;
END $$;

-- ─────────────────────────────────────────────────────────────────────────────
-- Change 2: tournaments.disputes.evidence storage bucket — expand limits
--
-- Original bucket was created with 5 MB limit and no PDF support.
-- Expand to 50 MB (52428800 bytes) and add application/pdf.
-- UPDATE is idempotent: re-running sets the row to the same values.
-- ─────────────────────────────────────────────────────────────────────────────

UPDATE storage.buckets
SET file_size_limit    = 52428800,
    allowed_mime_types = ARRAY[
        'image/jpeg',
        'image/jpg',
        'image/png',
        'image/webp',
        'image/gif',
        'application/pdf'
    ]
WHERE id = 'tournaments.disputes.evidence';

-- ─────────────────────────────────────────────────────────────────────────────
-- Change 3: tournament_disputes.reopen_count
--
-- Tracks how many times a dispute has been reopened after initial resolution.
-- Used by the dispute centre UI to surface repeat disputes to admins.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE public.tournament_disputes
    ADD COLUMN IF NOT EXISTS reopen_count INTEGER NOT NULL DEFAULT 0;
