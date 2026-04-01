-- Add currency column to venues table
ALTER TABLE venues ADD COLUMN IF NOT EXISTS currency TEXT DEFAULT 'USD';
