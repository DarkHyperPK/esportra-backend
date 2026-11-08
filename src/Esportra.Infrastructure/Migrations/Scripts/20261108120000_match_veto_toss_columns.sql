-- Add server-side coin-toss support to tournament match room map veto.
-- Three nullable columns: who won the toss, when it was done, and who acts first.

ALTER TABLE public.match_map_vetos
    ADD COLUMN IF NOT EXISTS toss_winner_team_id     uuid REFERENCES public.teams(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS toss_completed_at        timestamptz,
    ADD COLUMN IF NOT EXISTS toss_first_actor_team_id uuid REFERENCES public.teams(id) ON DELETE SET NULL;
