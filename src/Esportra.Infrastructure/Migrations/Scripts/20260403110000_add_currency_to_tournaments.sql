-- Add currency column to tournaments table (default USD)
ALTER TABLE tournaments ADD COLUMN IF NOT EXISTS currency TEXT DEFAULT 'USD';
