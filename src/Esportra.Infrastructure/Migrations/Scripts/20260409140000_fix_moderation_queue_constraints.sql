-- ============================================================================
-- Migration: Fix Moderation Queue Constraints & RLS
-- Purpose:   Add CHECK constraints and harden the authenticated INSERT policy
-- ============================================================================

-- ── CHECK constraints ──────────────────────────────────────────────────────
-- Add CHECK on content_type to enforce valid enum values
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.check_constraints
    WHERE constraint_name = 'moderation_queue_content_type_check'
  ) THEN
    ALTER TABLE moderation_queue
      ADD CONSTRAINT moderation_queue_content_type_check
      CHECK (content_type IN ('tournament', 'team', 'profile', 'match_evidence'));
  END IF;
END; $$;

-- Add CHECK on status to enforce valid enum values
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.check_constraints
    WHERE constraint_name = 'moderation_queue_status_check'
  ) THEN
    ALTER TABLE moderation_queue
      ADD CONSTRAINT moderation_queue_status_check
      CHECK (status IN ('pending', 'approved', 'rejected'));
  END IF;
END; $$;

-- ── RLS: harden authenticated INSERT policy ────────────────────────────────
-- Drop and recreate to enforce invariants: new items must be pending,
-- unreviewed, and owned by the inserting user.
DROP POLICY IF EXISTS moderation_queue_authenticated_insert ON moderation_queue;

CREATE POLICY moderation_queue_authenticated_insert ON moderation_queue
  FOR INSERT TO authenticated WITH CHECK (
      status = 'pending'
      AND reviewed_by IS NULL
      AND reviewed_at IS NULL
      AND review_notes = ''
      AND reported_by = auth.uid()
  );
