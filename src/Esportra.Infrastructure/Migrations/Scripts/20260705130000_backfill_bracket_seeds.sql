-- Backfill team seeds for existing round 1 (round_index = 0) matches
-- that were created before seed columns were added.
--
-- This uses a placeholder formula (match_number * 2 - 1 for slot 1, match_number * 2 for slot 2).
-- It won't match the original standard bracket seeding, but it ensures seeds exist
-- so they can propagate correctly when teams advance.
--
-- For accurate seeds, regenerate the bracket or manually update seeds via the organizer UI.

UPDATE brkt_matches
SET
    team1_seed = CASE
        WHEN team1_id IS NOT NULL AND team1_seed IS NULL
        THEN match_number * 2 - 1
        ELSE team1_seed
    END,
    team2_seed = CASE
        WHEN team2_id IS NOT NULL AND team2_seed IS NULL
        THEN match_number * 2
        ELSE team2_seed
    END
WHERE round_index = 0
  AND (team1_seed IS NULL OR team2_seed IS NULL);
