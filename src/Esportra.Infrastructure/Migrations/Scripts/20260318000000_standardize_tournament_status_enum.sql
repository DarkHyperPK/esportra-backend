-- Standardize tournament_status enum:
-- Add 'published' (visible but registration not open)
-- Remove 'check_in' (no longer used)
--
-- Final enum: draft | published | open | closed | ongoing | completed | cancelled

-- Step 1: Migrate any existing 'check_in' rows to 'ongoing'
UPDATE tournaments SET status = 'ongoing' WHERE status = 'check_in';

-- Step 2: Add 'published' to the enum
ALTER TYPE tournament_status ADD VALUE IF NOT EXISTS 'published' AFTER 'draft';

-- Note: PostgreSQL does not support removing enum values directly.
-- 'check_in' will remain in the enum type but is no longer used by the application.
-- To fully remove it would require recreating the type and all dependent columns,
-- which is a destructive operation best avoided in production.
