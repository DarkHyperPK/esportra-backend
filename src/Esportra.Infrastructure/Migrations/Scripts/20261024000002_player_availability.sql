-- PROJ-065 Competitive Phase 1: player availability
-- Two tables: recurring weekly rules and one-off date exceptions.

-- ── player_availability_rules ─────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.player_availability_rules (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id         UUID        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    game_slug       TEXT,
    day_of_week     INT         NOT NULL,
    start_time      TIME        NOT NULL,
    end_time        TIME        NOT NULL,
    timezone        TEXT        NOT NULL,
    effective_from  DATE,
    effective_until DATE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- CHECK: end_time must be after start_time (guarded)
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'chk_par_time'
    ) THEN
        ALTER TABLE public.player_availability_rules
            ADD CONSTRAINT chk_par_time CHECK (end_time > start_time);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_par_user
    ON public.player_availability_rules (user_id);

ALTER TABLE public.player_availability_rules ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS par_own ON public.player_availability_rules;
CREATE POLICY par_own
    ON public.player_availability_rules
    FOR ALL
    TO authenticated
    USING  (user_id = auth.uid())
    WITH CHECK (user_id = auth.uid());

DROP POLICY IF EXISTS par_service ON public.player_availability_rules;
CREATE POLICY par_service
    ON public.player_availability_rules
    FOR ALL
    TO service_role
    USING (true)
    WITH CHECK (true);

-- ── player_availability_exceptions ────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.player_availability_exceptions (
    id             UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id        UUID        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    exception_date DATE        NOT NULL,
    is_available   BOOLEAN     NOT NULL DEFAULT FALSE,
    start_time     TIME,
    end_time       TIME,
    note           TEXT,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Named unique constraint (guarded)
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'pae_user_date_unique'
    ) THEN
        ALTER TABLE public.player_availability_exceptions
            ADD CONSTRAINT pae_user_date_unique UNIQUE (user_id, exception_date);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_pae_user
    ON public.player_availability_exceptions (user_id);

ALTER TABLE public.player_availability_exceptions ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS pae_own ON public.player_availability_exceptions;
CREATE POLICY pae_own
    ON public.player_availability_exceptions
    FOR ALL
    TO authenticated
    USING  (user_id = auth.uid())
    WITH CHECK (user_id = auth.uid());

DROP POLICY IF EXISTS pae_service ON public.player_availability_exceptions;
CREATE POLICY pae_service
    ON public.player_availability_exceptions
    FOR ALL
    TO service_role
    USING (true)
    WITH CHECK (true);
