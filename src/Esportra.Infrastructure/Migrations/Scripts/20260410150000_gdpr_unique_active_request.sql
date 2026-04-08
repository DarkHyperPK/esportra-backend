-- Migration: 20260410150000_gdpr_unique_active_request.sql
-- Fix #6: Add partial unique index to prevent duplicate active GDPR requests (TOCTOU race)
-- Fix #7: Extend CHECK constraint to include 'rejected' status

-- ── Fix #7: Add 'rejected' to the status CHECK constraint ─────────────────
ALTER TABLE gdpr_requests DROP CONSTRAINT IF EXISTS gdpr_requests_status_check;
ALTER TABLE gdpr_requests ADD CONSTRAINT gdpr_requests_status_check
    CHECK (status IN ('pending', 'processing', 'completed', 'failed', 'cancelled', 'rejected'));

-- ── Fix #6: Partial unique index — at most one active request per user per type ─
-- Replaces the application-level TOCTOU check with a DB-level guarantee
CREATE UNIQUE INDEX IF NOT EXISTS uq_gdpr_active_request
    ON gdpr_requests(user_id, request_type)
    WHERE status IN ('pending', 'processing');
