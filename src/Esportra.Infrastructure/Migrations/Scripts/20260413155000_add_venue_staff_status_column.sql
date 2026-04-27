-- Add status column to venue_staff BEFORE other migrations reference it in RLS policies.
-- The staff_permissions migration (20260413220000) also adds this column with IF NOT EXISTS,
-- so this is safe to run first.
ALTER TABLE venue_staff ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'active';

-- Backfill: staff with accepted_at set are 'active', others are 'pending'
UPDATE venue_staff SET status = 'active' WHERE accepted_at IS NOT NULL AND status = 'active';
UPDATE venue_staff SET status = 'pending' WHERE accepted_at IS NULL AND status = 'active';
