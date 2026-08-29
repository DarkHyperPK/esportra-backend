-- Remove permanently-failed Hangfire job for the deleted InvitationExpiryJob class.
-- The class was removed but the job record (id=11488) remained, causing endless failure retries.
-- Guard: hangfire schema is created by the API on first startup, not by DbUp.
DO $$
BEGIN
  IF EXISTS (
    SELECT FROM information_schema.tables
    WHERE table_schema = 'hangfire' AND table_name = 'job'
  ) THEN
    DELETE FROM hangfire.job
    WHERE id = 11488
      AND invocationdata LIKE '%InvitationExpiryJob%';
  END IF;
END $$;
