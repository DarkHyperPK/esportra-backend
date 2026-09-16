-- Description: Add notification_job_id to match_chat_reads for Hangfire delayed chat notifications
-- This column stores the Hangfire job ID of the pending sliding-window chat notification job
-- so the ChatHub can cancel and reschedule it on each new message. Written by service_role only.
-- No index needed -- lookups are by PK (match_id, user_id).
ALTER TABLE public.match_chat_reads
    ADD COLUMN IF NOT EXISTS notification_job_id text NULL;
