-- Classify teams as real team, solo adapter, or mock simulation row.
-- Enables filtering user-facing team management from bracket adapter rows.

ALTER TABLE public.teams
    ADD COLUMN IF NOT EXISTS team_kind text NOT NULL DEFAULT 'team';

-- Backfill mock before solo (some mock rows may also have is_solo = true)
UPDATE public.teams
SET team_kind = 'mock'
WHERE tag LIKE 'mock-%'
   OR EXISTS (
       SELECT 1
       FROM public.tournament_participants tp
       WHERE tp.team_id = teams.id
         AND COALESCE(tp.is_mock, false) = true
   );

UPDATE public.teams
SET team_kind = 'solo'
WHERE team_kind = 'team'
  AND COALESCE(is_solo, false) = true;

UPDATE public.teams
SET team_kind = 'team'
WHERE team_kind NOT IN ('team', 'solo', 'mock');

ALTER TABLE public.teams
    DROP CONSTRAINT IF EXISTS chk_teams_team_kind;

ALTER TABLE public.teams
    ADD CONSTRAINT chk_teams_team_kind
    CHECK (team_kind IN ('team', 'solo', 'mock'));

ALTER TABLE public.teams
    DROP CONSTRAINT IF EXISTS chk_teams_kind_solo_consistency;

ALTER TABLE public.teams
    ADD CONSTRAINT chk_teams_kind_solo_consistency
    CHECK (
        (team_kind = 'solo' AND is_solo = true AND max_members = 1)
        OR (team_kind <> 'solo')
    );

-- Solo/mock adapters should not inherit misleading squad default
ALTER TABLE public.teams
    ALTER COLUMN game_format DROP DEFAULT;

UPDATE public.teams
SET game_format = NULL
WHERE team_kind IN ('solo', 'mock');

CREATE INDEX IF NOT EXISTS idx_teams_team_kind ON public.teams (team_kind);

GRANT SELECT, INSERT, UPDATE, DELETE ON public.teams TO service_role;
