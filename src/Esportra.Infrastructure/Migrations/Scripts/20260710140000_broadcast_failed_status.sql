-- Add 'failed' status to broadcasts table

ALTER TABLE public.broadcasts DROP CONSTRAINT IF EXISTS broadcasts_status_check;

ALTER TABLE public.broadcasts
    ADD CONSTRAINT broadcasts_status_check
    CHECK (status IN ('draft', 'scheduled', 'sending', 'sent', 'cancelled', 'failed'));
