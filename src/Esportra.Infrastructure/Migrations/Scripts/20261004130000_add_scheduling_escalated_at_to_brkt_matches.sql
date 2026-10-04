ALTER TABLE public.brkt_matches
    ADD COLUMN IF NOT EXISTS scheduling_escalated_at timestamptz;
