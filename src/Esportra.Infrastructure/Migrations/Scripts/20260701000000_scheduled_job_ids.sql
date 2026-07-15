-- Add columns to store Hangfire job IDs for deadline-triggered scheduled jobs.
-- These allow cancellation/rescheduling when deadlines change.
ALTER TABLE tournaments ADD COLUMN IF NOT EXISTS checkin_job_id TEXT;
ALTER TABLE brkt_matches ADD COLUMN IF NOT EXISTS walkover_job_id TEXT;
