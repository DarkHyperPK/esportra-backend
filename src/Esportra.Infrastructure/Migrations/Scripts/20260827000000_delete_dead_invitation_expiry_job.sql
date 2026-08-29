-- Remove permanently-failed Hangfire job for the deleted InvitationExpiryJob class.
-- The class was removed but the job record (id=11488) remained, causing endless failure retries.
DELETE FROM hangfire.job
WHERE id = 11488
  AND invocation_data LIKE '%InvitationExpiryJob%';
