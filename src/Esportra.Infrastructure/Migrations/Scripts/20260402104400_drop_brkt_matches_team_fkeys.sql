-- Drop foreign key constraints on brkt_matches team columns
-- These referenced teams(id) but bracket matches need to support both
-- team IDs (from teams table) and participant IDs (from tournament_participants)
-- for solo tournament formats like 1v1 fighting games.

ALTER TABLE brkt_matches DROP CONSTRAINT IF EXISTS brkt_matches_team1_id_fkey;
ALTER TABLE brkt_matches DROP CONSTRAINT IF EXISTS brkt_matches_team2_id_fkey;
ALTER TABLE brkt_matches DROP CONSTRAINT IF EXISTS brkt_matches_winner_id_fkey;
ALTER TABLE brkt_matches DROP CONSTRAINT IF EXISTS brkt_matches_loser_id_fkey;
