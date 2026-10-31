-- PROJ-078: scrim history privacy flag on teams.
-- Lets a team opt their completed scrim history out of public view.
ALTER TABLE public.teams
    ADD COLUMN IF NOT EXISTS scrim_history_public boolean NOT NULL DEFAULT true;
