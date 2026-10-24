-- Separate team ownership from captaincy.
-- A. Add 'manager' role to the team_member_role enum.
-- B. Add captain_id to teams so ownership queries do not need a team_members join.
-- C. Backfill captain_id from the current captain row in team_members.
-- D. Partial index on captain_id for fast lookups.

-- A. Extend enum: guard via pg_enum so replay is safe.
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_enum
    WHERE enumlabel = 'manager'
      AND enumtypid = (SELECT oid FROM pg_type WHERE typname = 'team_member_role')
  ) THEN
    ALTER TYPE public.team_member_role ADD VALUE 'manager';
  END IF;
END $$;

-- B. Add captain_id column.
ALTER TABLE public.teams
    ADD COLUMN IF NOT EXISTS captain_id UUID REFERENCES auth.users(id);

-- C. Backfill captain_id from current captain row.
-- PRE-FLIGHT: Run on production before deploying, confirm expected count before proceeding:
-- SELECT COUNT(*) FROM public.teams WHERE captain_id IS NULL;
-- Expected: count equals teams that have a captain row in team_members (plus any teams with no captain)
UPDATE public.teams t
SET captain_id = (
    SELECT tm.user_id
    FROM public.team_members tm
    WHERE tm.team_id = t.id
      AND tm.role = 'captain'
    LIMIT 1
)
WHERE t.captain_id IS NULL;

-- D. Partial index: only index rows where captain_id is set.
CREATE INDEX IF NOT EXISTS idx_teams_captain_id
    ON public.teams(captain_id)
    WHERE captain_id IS NOT NULL;
