-- Add job ID tracking for per-invitation expiry jobs
ALTER TABLE tournament_invitations ADD COLUMN IF NOT EXISTS expiry_job_id TEXT;

-- Index for efficient job lookups during cancellation
CREATE INDEX IF NOT EXISTS idx_invitations_expiry_job ON tournament_invitations(expiry_job_id) WHERE expiry_job_id IS NOT NULL;
