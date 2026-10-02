-- PROJ-044 T1: Toss system for public_veto_sessions.
--
-- Adds 'pending_toss' and 'toss_choice_pending' to the status CHECK constraint,
-- changes the status DEFAULT to 'pending_toss', and adds three nullable toss
-- state columns. No backfill — existing sessions where toss_first_actor_team_id
-- IS NULL fall back to team1_id/team2_id in the backend sequence resolver.

-- 1. Extend the status CHECK constraint.
--    Pattern: DROP CONSTRAINT IF EXISTS (constraint replacement) then re-add
--    inside a DO $$ guard (CLAUDE.md: ADD CONSTRAINT has no IF NOT EXISTS).
ALTER TABLE public.public_veto_sessions
    DROP CONSTRAINT IF EXISTS public_veto_sessions_status_check;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'public_veto_sessions_status_check'
    ) THEN
        ALTER TABLE public.public_veto_sessions
            ADD CONSTRAINT public_veto_sessions_status_check
            CHECK (status IN ('in_progress', 'completed', 'cancelled', 'pending_toss', 'toss_choice_pending'));
    END IF;
END $$;

-- 2. Change status DEFAULT from 'in_progress' to 'pending_toss'.
--    ALTER COLUMN ... SET DEFAULT is idempotent; safe to run multiple times.
ALTER TABLE public.public_veto_sessions
    ALTER COLUMN status SET DEFAULT 'pending_toss';

-- 3. Toss result: which team won the CSPRNG flip (nullable until toss runs).
ALTER TABLE public.public_veto_sessions
    ADD COLUMN IF NOT EXISTS toss_winner_team_id uuid;

-- 4. Timestamp when the flip ran (nullable until toss runs).
ALTER TABLE public.public_veto_sessions
    ADD COLUMN IF NOT EXISTS toss_completed_at timestamptz;

-- 5. Which team acts first in the veto (set at toss-choice time; may differ from
--    winner if they elected to give first action to opponent). Nullable until
--    the toss-choice endpoint resolves it. No FK — these are virtual team IDs
--    stored in the same row (team1_id / team2_id columns).
ALTER TABLE public.public_veto_sessions
    ADD COLUMN IF NOT EXISTS toss_first_actor_team_id uuid;
