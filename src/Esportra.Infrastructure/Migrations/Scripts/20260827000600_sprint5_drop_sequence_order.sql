-- L6: Drop vestigial sequence_order column from tournament_stages
-- stage_order (integer, default 1) is the live column; sequence_order is never written
ALTER TABLE tournament_stages DROP COLUMN IF EXISTS sequence_order;
