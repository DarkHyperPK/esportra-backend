-- PROJ-066 Competitive Phase 2: Team Recruitment (recruitment_openings, applications, events + RLS)
-- recruitment_openings, recruitment_applications, recruitment_application_events
-- with RLS (default deny, explicit allow).

-- ── Enum types ─────────────────────────────────────────────────────────────────

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'recruitment_opening_status') THEN
        CREATE TYPE public.recruitment_opening_status AS ENUM (
            'draft', 'open', 'paused', 'filled', 'closed', 'expired', 'moderated'
        );
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'recruitment_opening_mode') THEN
        CREATE TYPE public.recruitment_opening_mode AS ENUM (
            'quick', 'structured'
        );
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'recruitment_application_status') THEN
        CREATE TYPE public.recruitment_application_status AS ENUM (
            'submitted', 'under_review', 'shortlisted', 'trial_invited',
            'offer_extended', 'accepted', 'withdrawn', 'rejected',
            'expired', 'archived', 'removed'
        );
    END IF;
END $$;

-- ── recruitment_openings ───────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.recruitment_openings (
    id                   UUID                            PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id              UUID                            NOT NULL REFERENCES public.teams(id) ON DELETE CASCADE,
    game_slug            TEXT                            NOT NULL,
    title                TEXT                            NOT NULL,
    description          TEXT,
    requirements         TEXT[]                          NOT NULL DEFAULT ARRAY[]::TEXT[],
    positions_available  INT                             NOT NULL DEFAULT 1,
    positions_filled     INT                             NOT NULL DEFAULT 0,
    mode                 recruitment_opening_mode        NOT NULL DEFAULT 'structured',
    tournament_id        UUID                            REFERENCES public.tournaments(id) ON DELETE SET NULL,
    status               recruitment_opening_status      NOT NULL DEFAULT 'draft',
    created_by           UUID                            NOT NULL REFERENCES auth.users(id),
    expires_at           TIMESTAMPTZ                     NOT NULL DEFAULT (now() + INTERVAL '30 days'),
    created_at           TIMESTAMPTZ                     NOT NULL DEFAULT now(),
    updated_at           TIMESTAMPTZ                     NOT NULL DEFAULT now()
);

-- CHECK: positions_filled must not exceed positions_available (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_recruitment_positions') THEN
        ALTER TABLE public.recruitment_openings
            ADD CONSTRAINT chk_recruitment_positions
            CHECK (positions_filled <= positions_available AND positions_available >= 1);
    END IF;
END $$;

-- CHECK: description max 2000 chars (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_recruitment_opening_description_len') THEN
        ALTER TABLE public.recruitment_openings
            ADD CONSTRAINT chk_recruitment_opening_description_len
            CHECK (description IS NULL OR length(description) <= 2000);
    END IF;
END $$;

-- CHECK: title non-empty (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_recruitment_opening_title_len') THEN
        ALTER TABLE public.recruitment_openings
            ADD CONSTRAINT chk_recruitment_opening_title_len
            CHECK (length(title) BETWEEN 1 AND 200);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_recruitment_openings_team
    ON public.recruitment_openings (team_id);

CREATE INDEX IF NOT EXISTS idx_recruitment_openings_game_status
    ON public.recruitment_openings (game_slug)
    WHERE status = 'open';

CREATE INDEX IF NOT EXISTS idx_recruitment_openings_expires
    ON public.recruitment_openings (expires_at)
    WHERE status = 'open';

ALTER TABLE public.recruitment_openings ENABLE ROW LEVEL SECURITY;

-- Team staff (captain or manager) can manage their own team's openings.
DROP POLICY IF EXISTS recruitment_openings_staff ON public.recruitment_openings;
CREATE POLICY recruitment_openings_staff
    ON public.recruitment_openings
    FOR ALL
    TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.team_members tm
            WHERE tm.team_id = recruitment_openings.team_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    )
    WITH CHECK (
        EXISTS (
            SELECT 1 FROM public.team_members tm
            WHERE tm.team_id = recruitment_openings.team_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    );

-- Public: any authenticated user can see open openings.
DROP POLICY IF EXISTS recruitment_openings_public ON public.recruitment_openings;
CREATE POLICY recruitment_openings_public
    ON public.recruitment_openings
    FOR SELECT
    TO authenticated
    USING (status = 'open');

-- Service role: unrestricted.
DROP POLICY IF EXISTS recruitment_openings_service ON public.recruitment_openings;
CREATE POLICY recruitment_openings_service
    ON public.recruitment_openings
    FOR ALL
    TO service_role
    USING (true)
    WITH CHECK (true);

-- ── recruitment_applications ───────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.recruitment_applications (
    id                  UUID                              PRIMARY KEY DEFAULT gen_random_uuid(),
    opening_id          UUID                              NOT NULL REFERENCES public.recruitment_openings(id) ON DELETE CASCADE,
    applicant_user_id   UUID                              NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    status              recruitment_application_status    NOT NULL DEFAULT 'submitted',
    cover_letter        TEXT,
    created_at          TIMESTAMPTZ                       NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ                       NOT NULL DEFAULT now()
);

-- One application per applicant per opening (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_recruitment_application_per_opening') THEN
        ALTER TABLE public.recruitment_applications
            ADD CONSTRAINT uq_recruitment_application_per_opening
            UNIQUE (opening_id, applicant_user_id);
    END IF;
END $$;

-- CHECK: cover_letter max 1000 chars (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_recruitment_cover_letter_len') THEN
        ALTER TABLE public.recruitment_applications
            ADD CONSTRAINT chk_recruitment_cover_letter_len
            CHECK (cover_letter IS NULL OR length(cover_letter) <= 1000);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_recruitment_applications_opening
    ON public.recruitment_applications (opening_id);

CREATE INDEX IF NOT EXISTS idx_recruitment_applications_applicant
    ON public.recruitment_applications (applicant_user_id);

ALTER TABLE public.recruitment_applications ENABLE ROW LEVEL SECURITY;

-- Applicant can manage their own applications.
DROP POLICY IF EXISTS recruitment_applications_applicant ON public.recruitment_applications;
CREATE POLICY recruitment_applications_applicant
    ON public.recruitment_applications
    FOR ALL
    TO authenticated
    USING (applicant_user_id = auth.uid())
    WITH CHECK (applicant_user_id = auth.uid());

-- Team staff can see and manage applications for their team's openings.
DROP POLICY IF EXISTS recruitment_applications_staff ON public.recruitment_applications;
CREATE POLICY recruitment_applications_staff
    ON public.recruitment_applications
    FOR ALL
    TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.recruitment_openings ro
            JOIN public.team_members tm ON tm.team_id = ro.team_id
            WHERE ro.id = recruitment_applications.opening_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    );

-- Service role: unrestricted.
DROP POLICY IF EXISTS recruitment_applications_service ON public.recruitment_applications;
CREATE POLICY recruitment_applications_service
    ON public.recruitment_applications
    FOR ALL
    TO service_role
    USING (true)
    WITH CHECK (true);

-- ── recruitment_application_events ────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.recruitment_application_events (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    application_id  UUID        NOT NULL REFERENCES public.recruitment_applications(id) ON DELETE CASCADE,
    actor_user_id   UUID        NOT NULL REFERENCES auth.users(id),
    event_type      TEXT        NOT NULL,
    note            TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- CHECK: note max 500 chars (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_recruitment_event_note_len') THEN
        ALTER TABLE public.recruitment_application_events
            ADD CONSTRAINT chk_recruitment_event_note_len
            CHECK (note IS NULL OR length(note) <= 500);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_recruitment_events_application
    ON public.recruitment_application_events (application_id);

ALTER TABLE public.recruitment_application_events ENABLE ROW LEVEL SECURITY;

-- Applicant and team staff can see events for applications they are party to.
DROP POLICY IF EXISTS recruitment_events_access ON public.recruitment_application_events;
CREATE POLICY recruitment_events_access
    ON public.recruitment_application_events
    FOR ALL
    TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.recruitment_applications ra
            JOIN public.recruitment_openings ro ON ro.id = ra.opening_id
            WHERE ra.id = recruitment_application_events.application_id
              AND (
                  ra.applicant_user_id = auth.uid()
                  OR EXISTS (
                      SELECT 1 FROM public.team_members tm
                      WHERE tm.team_id = ro.team_id
                        AND tm.user_id = auth.uid()
                        AND tm.role IN ('captain', 'manager')
                        AND tm.is_active = TRUE
                  )
              )
        )
    );

-- Service role: unrestricted.
DROP POLICY IF EXISTS recruitment_events_service ON public.recruitment_application_events;
CREATE POLICY recruitment_events_service
    ON public.recruitment_application_events
    FOR ALL
    TO service_role
    USING (true)
    WITH CHECK (true);

-- ── Feature flags (Phase 2) ────────────────────────────────────────────────────

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_schema = 'public' AND table_name = 'feature_flags'
    ) THEN
        INSERT INTO public.feature_flags (key, name, description, flag_type, default_value, is_enabled)
        VALUES
            (
                'recruitment',
                'Team Recruitment',
                'Esportra Competitive Phase 2: team recruitment openings, applications and pipeline. Default off for beta.',
                'boolean',
                'false'::jsonb,
                FALSE
            )
        ON CONFLICT (key) DO NOTHING;
    END IF;
END $$;
