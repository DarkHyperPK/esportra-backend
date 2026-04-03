-- Add region column to tournaments table
ALTER TABLE tournaments ADD COLUMN IF NOT EXISTS region TEXT;
