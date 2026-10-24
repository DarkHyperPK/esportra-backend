-- PROJ-068 Competitive Phase 4: Scrims and Practice
-- scrim_listings, scrim_offers, scrim_sessions, scrim_result_reports,
-- scrim_disputes, practice_sessions, practice_attendance with RLS.

-- ── Enum types ─────────────────────────────────────────────────────────────────

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'scrim_listing_status') THEN
        CREATE TYPE public.scrim_listing_status AS ENUM ('open', 'paused', 'closed');
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'scrim_offer_status') THEN
        CREATE TYPE public.scrim_offer_status AS ENUM (
            'pending', 'accepted', 'declined', 'withdrawn', 'expired'
        );
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'scrim_session_status') THEN
        CREATE TYPE public.scrim_session_status AS ENUM (
            'scheduled', 'in_progress', 'completed', 'cancelled', 'disputed'
        );
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'scrim_dispute_status') THEN
        CREATE TYPE public.scrim_dispute_status AS ENUM ('open', 'resolved', 'escalated');
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'practice_session_status') THEN
        CREATE TYPE public.practice_session_status AS ENUM ('scheduled', 'completed', 'cancelled');
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'practice_attendance_status') THEN
        CREATE TYPE public.practice_attendance_status AS ENUM (
            'invited', 'confirmed', 'attended', 'absent', 'excused'
        );
    END IF;
END $$;

-- ── scrim_listings ─────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.scrim_listings (
    id                      UUID                    PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id                 UUID                    NOT NULL REFERENCES public.teams(id) ON DELETE CASCADE,
    game_slug               TEXT                    NOT NULL,
    mode_key                TEXT,
    region_key              TEXT,
    min_rank_key            TEXT,
    max_rank_key            TEXT,
    preferred_days          INT[]                   NOT NULL DEFAULT ARRAY[]::INT[],
    preferred_time_start    TIME,
    preferred_time_end      TIME,
    timezone                TEXT,
    notes                   TEXT,
    status                  scrim_listing_status    NOT NULL DEFAULT 'open',
    created_at              TIMESTAMPTZ             NOT NULL DEFAULT now(),
    updated_at              TIMESTAMPTZ             NOT NULL DEFAULT now()
);

-- CHECK: notes max 500 (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_listing_notes_len') THEN
        ALTER TABLE public.scrim_listings
            ADD CONSTRAINT chk_scrim_listing_notes_len
            CHECK (notes IS NULL OR length(notes) <= 500);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_scrim_listings_team ON public.scrim_listings (team_id);
CREATE INDEX IF NOT EXISTS idx_scrim_listings_game_status
    ON public.scrim_listings (game_slug) WHERE status = 'open';

ALTER TABLE public.scrim_listings ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS scrim_listings_staff ON public.scrim_listings;
CREATE POLICY scrim_listings_staff
    ON public.scrim_listings FOR ALL TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.team_members tm
            WHERE tm.team_id = scrim_listings.team_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    )
    WITH CHECK (
        EXISTS (
            SELECT 1 FROM public.team_members tm
            WHERE tm.team_id = scrim_listings.team_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    );

DROP POLICY IF EXISTS scrim_listings_public ON public.scrim_listings;
CREATE POLICY scrim_listings_public
    ON public.scrim_listings FOR SELECT TO authenticated
    USING (status = 'open');

DROP POLICY IF EXISTS scrim_listings_service ON public.scrim_listings;
CREATE POLICY scrim_listings_service
    ON public.scrim_listings FOR ALL TO service_role
    USING (true) WITH CHECK (true);

-- ── scrim_offers ───────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.scrim_offers (
    id                          UUID                    PRIMARY KEY DEFAULT gen_random_uuid(),
    listing_id                  UUID                    NOT NULL REFERENCES public.scrim_listings(id) ON DELETE CASCADE,
    offering_team_id            UUID                    NOT NULL REFERENCES public.teams(id) ON DELETE CASCADE,
    proposed_at                 TIMESTAMPTZ             NOT NULL DEFAULT now(),
    proposed_duration_minutes   INT,
    notes                       TEXT,
    status                      scrim_offer_status      NOT NULL DEFAULT 'pending',
    created_at                  TIMESTAMPTZ             NOT NULL DEFAULT now(),
    updated_at                  TIMESTAMPTZ             NOT NULL DEFAULT now()
);

-- CHECK: notes max 300 chars (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_offer_notes_len') THEN
        ALTER TABLE public.scrim_offers
            ADD CONSTRAINT chk_scrim_offer_notes_len
            CHECK (notes IS NULL OR length(notes) <= 300);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_scrim_offers_listing ON public.scrim_offers (listing_id);
CREATE INDEX IF NOT EXISTS idx_scrim_offers_offering_team ON public.scrim_offers (offering_team_id);

ALTER TABLE public.scrim_offers ENABLE ROW LEVEL SECURITY;

-- Listing team staff can see/accept/decline offers for their listing.
DROP POLICY IF EXISTS scrim_offers_listing_staff ON public.scrim_offers;
CREATE POLICY scrim_offers_listing_staff
    ON public.scrim_offers FOR ALL TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.scrim_listings sl
            JOIN public.team_members tm ON tm.team_id = sl.team_id
            WHERE sl.id = scrim_offers.listing_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    );

-- Offering team staff can manage their own offers.
DROP POLICY IF EXISTS scrim_offers_offering_staff ON public.scrim_offers;
CREATE POLICY scrim_offers_offering_staff
    ON public.scrim_offers FOR ALL TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.team_members tm
            WHERE tm.team_id = scrim_offers.offering_team_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    )
    WITH CHECK (
        EXISTS (
            SELECT 1 FROM public.team_members tm
            WHERE tm.team_id = scrim_offers.offering_team_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    );

DROP POLICY IF EXISTS scrim_offers_service ON public.scrim_offers;
CREATE POLICY scrim_offers_service
    ON public.scrim_offers FOR ALL TO service_role
    USING (true) WITH CHECK (true);

-- ── scrim_sessions ─────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.scrim_sessions (
    id                          UUID                    PRIMARY KEY DEFAULT gen_random_uuid(),
    team1_id                    UUID                    NOT NULL REFERENCES public.teams(id) ON DELETE CASCADE,
    team2_id                    UUID                    NOT NULL REFERENCES public.teams(id) ON DELETE CASCADE,
    game_slug                   TEXT                    NOT NULL,
    mode_key                    TEXT,
    scheduled_at                TIMESTAMPTZ,
    duration_minutes            INT,
    server_info                 TEXT,
    lobby_code                  TEXT,
    status                      scrim_session_status    NOT NULL DEFAULT 'scheduled',
    result_reported_by_team1    BOOLEAN                 NOT NULL DEFAULT FALSE,
    result_reported_by_team2    BOOLEAN                 NOT NULL DEFAULT FALSE,
    team1_score                 INT,
    team2_score                 INT,
    created_at                  TIMESTAMPTZ             NOT NULL DEFAULT now(),
    updated_at                  TIMESTAMPTZ             NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_scrim_sessions_team1 ON public.scrim_sessions (team1_id);
CREATE INDEX IF NOT EXISTS idx_scrim_sessions_team2 ON public.scrim_sessions (team2_id);
CREATE INDEX IF NOT EXISTS idx_scrim_sessions_status ON public.scrim_sessions (status);

ALTER TABLE public.scrim_sessions ENABLE ROW LEVEL SECURITY;

-- Both teams' staff can access the session.
DROP POLICY IF EXISTS scrim_sessions_teams ON public.scrim_sessions;
CREATE POLICY scrim_sessions_teams
    ON public.scrim_sessions FOR ALL TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.team_members tm
            WHERE tm.team_id IN (scrim_sessions.team1_id, scrim_sessions.team2_id)
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    );

DROP POLICY IF EXISTS scrim_sessions_service ON public.scrim_sessions;
CREATE POLICY scrim_sessions_service
    ON public.scrim_sessions FOR ALL TO service_role
    USING (true) WITH CHECK (true);

-- ── scrim_result_reports ───────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.scrim_result_reports (
    id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id          UUID        NOT NULL REFERENCES public.scrim_sessions(id) ON DELETE CASCADE,
    reporting_team_id   UUID        NOT NULL REFERENCES public.teams(id) ON DELETE CASCADE,
    team1_score         INT         NOT NULL,
    team2_score         INT         NOT NULL,
    notes               TEXT,
    screenshots_urls    TEXT[]      NOT NULL DEFAULT ARRAY[]::TEXT[],
    submitted_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- One report per team per session (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_scrim_result_report') THEN
        ALTER TABLE public.scrim_result_reports
            ADD CONSTRAINT uq_scrim_result_report
            UNIQUE (session_id, reporting_team_id);
    END IF;
END $$;

-- CHECK: notes max 500 chars (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_result_notes_len') THEN
        ALTER TABLE public.scrim_result_reports
            ADD CONSTRAINT chk_scrim_result_notes_len
            CHECK (notes IS NULL OR length(notes) <= 500);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_scrim_result_session ON public.scrim_result_reports (session_id);

ALTER TABLE public.scrim_result_reports ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS scrim_result_reports_teams ON public.scrim_result_reports;
CREATE POLICY scrim_result_reports_teams
    ON public.scrim_result_reports FOR ALL TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.scrim_sessions ss
            JOIN public.team_members tm ON tm.team_id IN (ss.team1_id, ss.team2_id)
            WHERE ss.id = scrim_result_reports.session_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    )
    WITH CHECK (
        EXISTS (
            SELECT 1 FROM public.team_members tm
            WHERE tm.team_id = scrim_result_reports.reporting_team_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    );

DROP POLICY IF EXISTS scrim_result_reports_service ON public.scrim_result_reports;
CREATE POLICY scrim_result_reports_service
    ON public.scrim_result_reports FOR ALL TO service_role
    USING (true) WITH CHECK (true);

-- ── scrim_disputes ─────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.scrim_disputes (
    id                  UUID                    PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id          UUID                    NOT NULL REFERENCES public.scrim_sessions(id) ON DELETE CASCADE,
    raised_by_team_id   UUID                    NOT NULL REFERENCES public.teams(id) ON DELETE CASCADE,
    reason              TEXT                    NOT NULL,
    status              scrim_dispute_status    NOT NULL DEFAULT 'open',
    resolution_note     TEXT,
    resolved_at         TIMESTAMPTZ,
    created_at          TIMESTAMPTZ             NOT NULL DEFAULT now()
);

-- CHECK: reason max 1000, resolution_note max 500 (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_dispute_reason_len') THEN
        ALTER TABLE public.scrim_disputes
            ADD CONSTRAINT chk_scrim_dispute_reason_len
            CHECK (length(reason) <= 1000);
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_dispute_resolution_len') THEN
        ALTER TABLE public.scrim_disputes
            ADD CONSTRAINT chk_scrim_dispute_resolution_len
            CHECK (resolution_note IS NULL OR length(resolution_note) <= 500);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_scrim_disputes_session ON public.scrim_disputes (session_id);

ALTER TABLE public.scrim_disputes ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS scrim_disputes_teams ON public.scrim_disputes;
CREATE POLICY scrim_disputes_teams
    ON public.scrim_disputes FOR ALL TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.scrim_sessions ss
            JOIN public.team_members tm ON tm.team_id IN (ss.team1_id, ss.team2_id)
            WHERE ss.id = scrim_disputes.session_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    );

DROP POLICY IF EXISTS scrim_disputes_service ON public.scrim_disputes;
CREATE POLICY scrim_disputes_service
    ON public.scrim_disputes FOR ALL TO service_role
    USING (true) WITH CHECK (true);

-- ── practice_sessions ─────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.practice_sessions (
    id              UUID                        PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id         UUID                        NOT NULL REFERENCES public.teams(id) ON DELETE CASCADE,
    game_slug       TEXT                        NOT NULL,
    title           TEXT                        NOT NULL,
    scheduled_at    TIMESTAMPTZ,
    duration_minutes INT,
    notes           TEXT,
    status          practice_session_status     NOT NULL DEFAULT 'scheduled',
    created_by      UUID                        NOT NULL REFERENCES auth.users(id),
    created_at      TIMESTAMPTZ                 NOT NULL DEFAULT now()
);

-- CHECK: title max 200, notes max 500 (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_practice_title_len') THEN
        ALTER TABLE public.practice_sessions
            ADD CONSTRAINT chk_practice_title_len
            CHECK (length(title) BETWEEN 1 AND 200);
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_practice_notes_len') THEN
        ALTER TABLE public.practice_sessions
            ADD CONSTRAINT chk_practice_notes_len
            CHECK (notes IS NULL OR length(notes) <= 500);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_practice_sessions_team ON public.practice_sessions (team_id);

ALTER TABLE public.practice_sessions ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS practice_sessions_team ON public.practice_sessions;
CREATE POLICY practice_sessions_team
    ON public.practice_sessions FOR ALL TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.team_members tm
            WHERE tm.team_id = practice_sessions.team_id
              AND tm.user_id = auth.uid()
              AND tm.is_active = TRUE
        )
    )
    WITH CHECK (
        EXISTS (
            SELECT 1 FROM public.team_members tm
            WHERE tm.team_id = practice_sessions.team_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    );

DROP POLICY IF EXISTS practice_sessions_service ON public.practice_sessions;
CREATE POLICY practice_sessions_service
    ON public.practice_sessions FOR ALL TO service_role
    USING (true) WITH CHECK (true);

-- ── practice_attendance ────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.practice_attendance (
    id          UUID                        PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id  UUID                        NOT NULL REFERENCES public.practice_sessions(id) ON DELETE CASCADE,
    user_id     UUID                        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    status      practice_attendance_status  NOT NULL DEFAULT 'invited'
);

-- One attendance row per user per session (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_practice_attendance') THEN
        ALTER TABLE public.practice_attendance
            ADD CONSTRAINT uq_practice_attendance UNIQUE (session_id, user_id);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_practice_attendance_session ON public.practice_attendance (session_id);
CREATE INDEX IF NOT EXISTS idx_practice_attendance_user ON public.practice_attendance (user_id);

ALTER TABLE public.practice_attendance ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS practice_attendance_member ON public.practice_attendance;
CREATE POLICY practice_attendance_member
    ON public.practice_attendance FOR ALL TO authenticated
    USING (
        user_id = auth.uid()
        OR EXISTS (
            SELECT 1 FROM public.practice_sessions ps
            JOIN public.team_members tm ON tm.team_id = ps.team_id
            WHERE ps.id = practice_attendance.session_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    );

DROP POLICY IF EXISTS practice_attendance_service ON public.practice_attendance;
CREATE POLICY practice_attendance_service
    ON public.practice_attendance FOR ALL TO service_role
    USING (true) WITH CHECK (true);

-- ── Feature flags (Phase 4) ────────────────────────────────────────────────────

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_schema = 'public' AND table_name = 'feature_flags'
    ) THEN
        INSERT INTO public.feature_flags (key, name, description, flag_type, default_value, is_enabled)
        VALUES
            (
                'scrims',
                'Scrims & Practice',
                'Esportra Competitive Phase 4: scrim listings, offers, sessions, result reporting and practice scheduling. Default off for beta.',
                'boolean',
                'false'::jsonb,
                FALSE
            )
        ON CONFLICT (key) DO NOTHING;
    END IF;
END $$;
