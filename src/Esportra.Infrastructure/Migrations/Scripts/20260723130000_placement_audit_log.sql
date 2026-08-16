-- Audit log for sponsor placement lifecycle events.
CREATE TABLE IF NOT EXISTS public.sponsor_placement_audit_log (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    placement_id UUID NOT NULL,
    sponsor_id UUID NOT NULL,
    tournament_id UUID,
    placement_zone TEXT NOT NULL,
    slot_number INT,
    action TEXT NOT NULL,
    performed_by UUID,
    details JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS sponsor_placement_audit_log_placement_idx
    ON public.sponsor_placement_audit_log (placement_id, created_at DESC);
CREATE INDEX IF NOT EXISTS sponsor_placement_audit_log_sponsor_idx
    ON public.sponsor_placement_audit_log (sponsor_id, created_at DESC);

ALTER TABLE public.sponsor_placement_audit_log ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'sponsor_placement_audit_service_role') THEN
    CREATE POLICY sponsor_placement_audit_service_role
        ON public.sponsor_placement_audit_log TO service_role USING (true) WITH CHECK (true);
  END IF;
END $$;
