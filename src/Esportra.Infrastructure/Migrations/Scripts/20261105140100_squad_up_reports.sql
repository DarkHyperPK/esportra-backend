-- Squad Up player reports with screenshot proof. Written and read only by the API (service role);
-- reporters and admins both go through endpoints that enforce who may see what.
CREATE TABLE IF NOT EXISTS public.squad_up_reports (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    reporter_id     UUID REFERENCES auth.users(id) ON DELETE SET NULL,
    reported_id     UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    match_id        UUID REFERENCES public.lfg_swipe_matches(id) ON DELETE SET NULL,
    context         TEXT NOT NULL DEFAULT 'card',
    reason          TEXT NOT NULL,
    details         TEXT NOT NULL DEFAULT '',
    proof_link      TEXT,
    snapshot        JSONB NOT NULL DEFAULT '{}'::jsonb,   -- reported player's name / Riot ID / region / tier at report time
    status          TEXT NOT NULL DEFAULT 'open',
    resolution      TEXT,
    resolution_note TEXT,
    resolved_by     UUID REFERENCES auth.users(id) ON DELETE SET NULL,
    resolved_at     TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_squad_up_reports_context') THEN
    ALTER TABLE public.squad_up_reports ADD CONSTRAINT chk_squad_up_reports_context CHECK (context IN ('card', 'match'));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_squad_up_reports_reason') THEN
    ALTER TABLE public.squad_up_reports ADD CONSTRAINT chk_squad_up_reports_reason
      CHECK (reason IN ('harassment', 'hate_speech', 'cheating', 'fake_rank', 'spam_scam', 'inappropriate_profile', 'other'));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_squad_up_reports_status') THEN
    ALTER TABLE public.squad_up_reports ADD CONSTRAINT chk_squad_up_reports_status CHECK (status IN ('open', 'resolved'));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_squad_up_reports_resolution') THEN
    ALTER TABLE public.squad_up_reports ADD CONSTRAINT chk_squad_up_reports_resolution
      CHECK (resolution IS NULL OR resolution IN ('dismissed', 'warned', 'squad_up_ban', 'suspended'));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_squad_up_reports_lengths') THEN
    ALTER TABLE public.squad_up_reports ADD CONSTRAINT chk_squad_up_reports_lengths
      CHECK (char_length(details) <= 1000 AND (proof_link IS NULL OR char_length(proof_link) <= 500));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_squad_up_reports_not_self') THEN
    ALTER TABLE public.squad_up_reports ADD CONSTRAINT chk_squad_up_reports_not_self CHECK (reporter_id IS DISTINCT FROM reported_id);
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_squad_up_reports_status ON public.squad_up_reports (status, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_squad_up_reports_reported ON public.squad_up_reports (reported_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_squad_up_reports_reporter ON public.squad_up_reports (reporter_id, created_at DESC);

CREATE TABLE IF NOT EXISTS public.squad_up_report_evidence (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    report_id    UUID NOT NULL REFERENCES public.squad_up_reports(id) ON DELETE CASCADE,
    storage_path TEXT NOT NULL,                 -- private bucket path; viewed only through short-lived signed URLs
    content_type TEXT NOT NULL,
    size_bytes   INT  NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_squad_up_report_evidence_report ON public.squad_up_report_evidence (report_id);

ALTER TABLE public.squad_up_reports ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS squad_up_reports_service ON public.squad_up_reports;
CREATE POLICY squad_up_reports_service ON public.squad_up_reports FOR ALL TO service_role USING (true) WITH CHECK (true);
REVOKE ALL ON public.squad_up_reports FROM anon, authenticated;

ALTER TABLE public.squad_up_report_evidence ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS squad_up_report_evidence_service ON public.squad_up_report_evidence;
CREATE POLICY squad_up_report_evidence_service ON public.squad_up_report_evidence FOR ALL TO service_role USING (true) WITH CHECK (true);
REVOKE ALL ON public.squad_up_report_evidence FROM anon, authenticated;
