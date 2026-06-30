-- Add roster-related columns to game_catalog_game_modes.
-- Split from 20260609200000_roster_member_role.sql since game_catalog tables
-- are created by 20260618000000_game_catalog_runtime.sql.

ALTER TABLE public.game_catalog_game_modes
    ADD COLUMN IF NOT EXISTS max_substitutes INT,
    ADD COLUMN IF NOT EXISTS allows_coaches BOOLEAN NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS max_coaches INT NOT NULL DEFAULT 2;

UPDATE public.game_catalog_game_modes
SET max_substitutes = GREATEST(COALESCE(max_roster_size, team_size) - team_size, 0)
WHERE max_substitutes IS NULL
  AND max_roster_size IS NOT NULL
  AND max_roster_size > team_size;
