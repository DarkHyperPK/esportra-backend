-- Targeted invitations reuse the existing hashed-token/owner RLS boundary.
ALTER TABLE public.broadcast_crew_invites ADD COLUMN IF NOT EXISTS recipient_email TEXT;
ALTER TABLE public.broadcast_crew_invites ADD COLUMN IF NOT EXISTS recipient_user_id UUID;
ALTER TABLE public.broadcast_crew_invites ADD COLUMN IF NOT EXISTS email_status TEXT;
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'broadcast_crew_invites_recipient_user_fk') THEN
    ALTER TABLE public.broadcast_crew_invites ADD CONSTRAINT broadcast_crew_invites_recipient_user_fk
      FOREIGN KEY (recipient_user_id) REFERENCES auth.users(id) ON DELETE CASCADE;
  END IF;
END $$;
CREATE INDEX IF NOT EXISTS broadcast_crew_invites_recipient_idx
  ON public.broadcast_crew_invites(recipient_user_id, expires_at) WHERE claimed_at IS NULL;
CREATE INDEX IF NOT EXISTS broadcast_crew_invites_email_idx
  ON public.broadcast_crew_invites(LOWER(recipient_email), expires_at) WHERE claimed_at IS NULL;
