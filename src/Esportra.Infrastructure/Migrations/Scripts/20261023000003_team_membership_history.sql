-- Create team_membership_history: permanent record of when users left teams.
-- Written by triggers or service RPCs; never by authenticated users directly.

CREATE TABLE IF NOT EXISTS public.team_membership_history (
    id          UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id     UUID        NOT NULL REFERENCES public.teams(id),
    user_id     UUID        NOT NULL REFERENCES auth.users(id),
    role        TEXT        NOT NULL,
    joined_at   TIMESTAMPTZ,                              -- may not be known for pre-history records
    left_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    left_reason TEXT,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_team_membership_history_team
    ON public.team_membership_history(team_id);

CREATE INDEX IF NOT EXISTS idx_team_membership_history_user
    ON public.team_membership_history(user_id);

ALTER TABLE public.team_membership_history ENABLE ROW LEVEL SECURITY;

-- A user may read their own membership history rows.
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'team_membership_history' AND policyname = 'tmh_own'
  ) THEN
    CREATE POLICY "tmh_own"
      ON public.team_membership_history
      FOR SELECT
      TO authenticated
      USING (user_id = auth.uid());
  END IF;
END $$;

-- Captains, owners, managers and coaches of the team may read all rows for their team.
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'team_membership_history' AND policyname = 'tmh_team_captain'
  ) THEN
    CREATE POLICY "tmh_team_captain"
      ON public.team_membership_history
      FOR SELECT
      TO authenticated
      USING (
        EXISTS (
          SELECT 1
          FROM public.team_members tm
          WHERE tm.team_id = team_membership_history.team_id
            AND tm.user_id = auth.uid()
            AND tm.role IN ('captain', 'owner', 'manager', 'coach')
        )
      );
  END IF;
END $$;

-- Service role has full access for API-side writes.
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'team_membership_history' AND policyname = 'tmh_service'
  ) THEN
    CREATE POLICY "tmh_service"
      ON public.team_membership_history
      FOR ALL
      TO service_role
      USING (true)
      WITH CHECK (true);
  END IF;
END $$;
