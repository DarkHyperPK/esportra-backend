-- Drop the unique constraint on riot_match_id in brkt_match_games.
-- This constraint prevents re-using the same Riot match across game rows
-- (e.g., after a match reset or when the same Riot match appears in
-- different contexts). The (match_id, game_number) key is sufficient.
ALTER TABLE public.brkt_match_games
    DROP CONSTRAINT IF EXISTS brkt_match_games_riot_match_id_key;
