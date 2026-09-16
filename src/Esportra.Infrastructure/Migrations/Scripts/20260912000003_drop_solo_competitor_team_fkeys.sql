-- These columns hold a polymorphic competitor_id: teams.id for team tournaments
-- and tournament_participants.id for solo tournaments. The solo-native migration
-- (20260610120000) rewrote existing data but missed dropping the teams-only FK
-- constraints on these tables, causing 23503 crashes for every solo participant
-- action beyond check-in. Same pattern as 20260609170000 (match_result_reports)
-- and 20260609160000 (match_messages).

-- Match finalization: FK violation inside the transaction aborts the whole tx,
-- causing match status to roll back to pending on every solo tournament.
ALTER TABLE public.match_completed_events
    DROP CONSTRAINT IF EXISTS match_completed_events_winner_id_fkey;
ALTER TABLE public.match_completed_events
    DROP CONSTRAINT IF EXISTS match_completed_events_loser_id_fkey;

-- Map veto: cannot be initialized or have actions recorded for solo tournaments.
ALTER TABLE public.match_map_vetos
    DROP CONSTRAINT IF EXISTS match_map_vetos_team1_id_fkey1;
ALTER TABLE public.match_map_vetos
    DROP CONSTRAINT IF EXISTS match_map_vetos_team2_id_fkey1;
ALTER TABLE public.match_map_vetos
    DROP CONSTRAINT IF EXISTS match_map_vetos_current_team_id_fkey1;

ALTER TABLE public.match_map_veto_actions
    DROP CONSTRAINT IF EXISTS match_map_veto_actions_team_id_fkey1;

-- Per-game scores: cannot be saved for solo tournaments.
ALTER TABLE public.brkt_match_games
    DROP CONSTRAINT IF EXISTS brkt_match_games_winner_id_fkey;
ALTER TABLE public.brkt_match_games
    DROP CONSTRAINT IF EXISTS brkt_match_games_loser_id_fkey;
ALTER TABLE public.brkt_match_games
    DROP CONSTRAINT IF EXISTS brkt_match_games_reported_by_team_id_fkey;

-- Tournament winner: cannot be set for solo tournaments.
ALTER TABLE public.tournaments
    DROP CONSTRAINT IF EXISTS tournaments_winner_id_fkey;

-- Prize / placement resolution: entire placement resolution crashes for solo.
ALTER TABLE public.tournament_placements
    DROP CONSTRAINT IF EXISTS tournament_placements_team_id_fkey;
ALTER TABLE public.tournament_cash_payouts
    DROP CONSTRAINT IF EXISTS tournament_cash_payouts_team_id_fkey;
ALTER TABLE public.tournament_reward_distributions
    DROP CONSTRAINT IF EXISTS tournament_reward_distributions_team_id_fkey;

-- Multi-stage advancement: fails when advancing solo participants to next stage.
ALTER TABLE public.stage_participants
    DROP CONSTRAINT IF EXISTS stage_participants_team_id_fkey;
