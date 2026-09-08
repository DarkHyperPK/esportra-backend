-- Add is_mock flag to teams so mock/virtual filler teams can be excluded from
-- leaderboard rankings. Mirrors the is_mock flag on tournament_participants.
-- Migration file was missing from the repository; the CI replay journal already
-- records it as applied, so this corrects the divergence between CI and production.

ALTER TABLE public.teams
    ADD COLUMN IF NOT EXISTS is_mock BOOLEAN NOT NULL DEFAULT FALSE;

CREATE INDEX IF NOT EXISTS idx_teams_is_mock
    ON public.teams USING btree (is_mock);
