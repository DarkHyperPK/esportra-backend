-- Hidden players (set by a reporter) and Squad Up restrictions (set by an admin).
CREATE TABLE IF NOT EXISTS public.squad_up_blocks (
    blocker_id       UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    blocked_id       UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    source_report_id UUID REFERENCES public.squad_up_reports(id) ON DELETE SET NULL,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (blocker_id, blocked_id)
);

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_squad_up_blocks_not_self') THEN
    ALTER TABLE public.squad_up_blocks ADD CONSTRAINT chk_squad_up_blocks_not_self CHECK (blocker_id <> blocked_id);
  END IF;
END $$;
CREATE INDEX IF NOT EXISTS idx_squad_up_blocks_blocked ON public.squad_up_blocks (blocked_id);

CREATE TABLE IF NOT EXISTS public.squad_up_restrictions (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id         UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    until           TIMESTAMPTZ,                       -- NULL = permanent
    reason_category TEXT NOT NULL,
    report_id       UUID REFERENCES public.squad_up_reports(id) ON DELETE SET NULL,
    created_by      UUID REFERENCES auth.users(id) ON DELETE SET NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    lifted_at       TIMESTAMPTZ,
    lifted_by       UUID REFERENCES auth.users(id) ON DELETE SET NULL
);
CREATE INDEX IF NOT EXISTS idx_squad_up_restrictions_active ON public.squad_up_restrictions (user_id) WHERE lifted_at IS NULL;

ALTER TABLE public.squad_up_blocks ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS squad_up_blocks_service ON public.squad_up_blocks;
CREATE POLICY squad_up_blocks_service ON public.squad_up_blocks FOR ALL TO service_role USING (true) WITH CHECK (true);
REVOKE ALL ON public.squad_up_blocks FROM anon, authenticated;

ALTER TABLE public.squad_up_restrictions ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS squad_up_restrictions_service ON public.squad_up_restrictions;
CREATE POLICY squad_up_restrictions_service ON public.squad_up_restrictions FOR ALL TO service_role USING (true) WITH CHECK (true);
REVOKE ALL ON public.squad_up_restrictions FROM anon, authenticated;
