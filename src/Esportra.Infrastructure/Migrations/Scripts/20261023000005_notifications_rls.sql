-- Enable RLS on the pre-baseline notifications table and add per-user + service policies.
-- The table has no RLS enabled out of the box; this corrects that with default-deny semantics.

ALTER TABLE public.notifications ENABLE ROW LEVEL SECURITY;

-- Users may read, insert, update, and delete only their own notifications.
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'notifications' AND policyname = 'notifications_own'
  ) THEN
    CREATE POLICY "notifications_own"
      ON public.notifications
      FOR ALL
      TO authenticated
      USING (user_id = auth.uid())
      WITH CHECK (user_id = auth.uid());
  END IF;
END $$;

-- Service role has unrestricted access for server-side writes and reads.
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'notifications' AND policyname = 'notifications_service'
  ) THEN
    CREATE POLICY "notifications_service"
      ON public.notifications
      FOR ALL
      TO service_role
      USING (true)
      WITH CHECK (true);
  END IF;
END $$;
