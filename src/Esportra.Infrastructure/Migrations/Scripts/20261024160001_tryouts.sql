-- PROJ-067 Competitive Phase 3: Tryout Sessions
-- tryout_sessions, tryout_participants, tryout_evaluations with RLS.

-- ── Enum types ─────────────────────────────────────────────────────────────────

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'tryout_session_status') THEN
        CREATE TYPE public.tryout_session_status AS ENUM (
            'draft', 'scheduled', 'in_progress', 'completed', 'cancelled'
        );
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'tryout_participant_status') THEN
        CREATE TYPE public.tryout_participant_status AS ENUM (
            'invited', 'confirmed', 'declined', 'attended', 'no_show', 'removed'
        );
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'tryout_recommendation') THEN
        CREATE TYPE public.tryout_recommendation AS ENUM (
            'offer', 'pass', 'undecided'
        );
    END IF;
END $$;

-- ── tryout_sessions ────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.tryout_sessions (
    id                  UUID                        PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id             UUID                        NOT NULL REFERENCES public.teams(id) ON DELETE CASCADE,
    game_slug           TEXT                        NOT NULL,
    title               TEXT                        NOT NULL,
    description         TEXT,
    scheduled_at        TIMESTAMPTZ,
    duration_minutes    INT,
    max_participants    INT,
    min_rank_key        TEXT,
    max_rank_key        TEXT,
    status              tryout_session_status       NOT NULL DEFAULT 'draft',
    created_by          UUID                        NOT NULL REFERENCES auth.users(id),
    created_at          TIMESTAMPTZ                 NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ                 NOT NULL DEFAULT now()
);

-- CHECK: title length (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_tryout_title_len') THEN
        ALTER TABLE public.tryout_sessions
            ADD CONSTRAINT chk_tryout_title_len
            CHECK (length(title) BETWEEN 1 AND 200);
    END IF;
END $$;

-- CHECK: description max 1000 chars (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_tryout_description_len') THEN
        ALTER TABLE public.tryout_sessions
            ADD CONSTRAINT chk_tryout_description_len
            CHECK (description IS NULL OR length(description) <= 1000);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_tryout_sessions_team
    ON public.tryout_sessions (team_id);

CREATE INDEX IF NOT EXISTS idx_tryout_sessions_scheduled
    ON public.tryout_sessions (scheduled_at)
    WHERE status IN ('scheduled', 'in_progress');

ALTER TABLE public.tryout_sessions ENABLE ROW LEVEL SECURITY;

-- Team staff (captain or manager) can manage their team's sessions.
DROP POLICY IF EXISTS tryout_sessions_staff ON public.tryout_sessions;
CREATE POLICY tryout_sessions_staff
    ON public.tryout_sessions
    FOR ALL
    TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.team_members tm
            WHERE tm.team_id = tryout_sessions.team_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    )
    WITH CHECK (
        EXISTS (
            SELECT 1 FROM public.team_members tm
            WHERE tm.team_id = tryout_sessions.team_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    );

-- Invited participants can read sessions they are invited to.
DROP POLICY IF EXISTS tryout_sessions_participant ON public.tryout_sessions;
CREATE POLICY tryout_sessions_participant
    ON public.tryout_sessions
    FOR SELECT
    TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.tryout_participants tp
            WHERE tp.session_id = tryout_sessions.id
              AND tp.user_id = auth.uid()
        )
    );

-- Service role: unrestricted.
DROP POLICY IF EXISTS tryout_sessions_service ON public.tryout_sessions;
CREATE POLICY tryout_sessions_service
    ON public.tryout_sessions
    FOR ALL
    TO service_role
    USING (true)
    WITH CHECK (true);

-- ── tryout_participants ────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.tryout_participants (
    id              UUID                            PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id      UUID                            NOT NULL REFERENCES public.tryout_sessions(id) ON DELETE CASCADE,
    user_id         UUID                            NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    invited_by      UUID                            NOT NULL REFERENCES auth.users(id),
    status          tryout_participant_status       NOT NULL DEFAULT 'invited',
    invited_at      TIMESTAMPTZ                     NOT NULL DEFAULT now(),
    responded_at    TIMESTAMPTZ,
    created_at      TIMESTAMPTZ                     NOT NULL DEFAULT now()
);

-- One participant row per user per session (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_tryout_participant') THEN
        ALTER TABLE public.tryout_participants
            ADD CONSTRAINT uq_tryout_participant UNIQUE (session_id, user_id);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_tryout_participants_session
    ON public.tryout_participants (session_id);

CREATE INDEX IF NOT EXISTS idx_tryout_participants_user
    ON public.tryout_participants (user_id);

ALTER TABLE public.tryout_participants ENABLE ROW LEVEL SECURITY;

-- Team staff can manage participants.
DROP POLICY IF EXISTS tryout_participants_staff ON public.tryout_participants;
CREATE POLICY tryout_participants_staff
    ON public.tryout_participants
    FOR ALL
    TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.tryout_sessions ts
            JOIN public.team_members tm ON tm.team_id = ts.team_id
            WHERE ts.id = tryout_participants.session_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    );

-- Participant can view and respond to their own invite.
DROP POLICY IF EXISTS tryout_participants_self ON public.tryout_participants;
CREATE POLICY tryout_participants_self
    ON public.tryout_participants
    FOR ALL
    TO authenticated
    USING (user_id = auth.uid())
    WITH CHECK (user_id = auth.uid());

-- Service role: unrestricted.
DROP POLICY IF EXISTS tryout_participants_service ON public.tryout_participants;
CREATE POLICY tryout_participants_service
    ON public.tryout_participants
    FOR ALL
    TO service_role
    USING (true)
    WITH CHECK (true);

-- ── tryout_evaluations ─────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.tryout_evaluations (
    id                  UUID                    PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id          UUID                    NOT NULL REFERENCES public.tryout_sessions(id) ON DELETE CASCADE,
    evaluatee_user_id   UUID                    NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    evaluator_user_id   UUID                    NOT NULL REFERENCES auth.users(id),
    overall_score       SMALLINT                NOT NULL CHECK (overall_score BETWEEN 1 AND 10),
    communication       SMALLINT                NOT NULL CHECK (communication BETWEEN 1 AND 10),
    mechanics           SMALLINT                NOT NULL CHECK (mechanics BETWEEN 1 AND 10),
    game_sense          SMALLINT                NOT NULL CHECK (game_sense BETWEEN 1 AND 10),
    attitude            SMALLINT                NOT NULL CHECK (attitude BETWEEN 1 AND 10),
    teamwork            SMALLINT                NOT NULL CHECK (teamwork BETWEEN 1 AND 10),
    peak_play           SMALLINT                NOT NULL CHECK (peak_play BETWEEN 1 AND 10),
    consistency         SMALLINT                NOT NULL CHECK (consistency BETWEEN 1 AND 10),
    notes               TEXT,
    recommendation      tryout_recommendation   NOT NULL DEFAULT 'undecided',
    created_at          TIMESTAMPTZ             NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ             NOT NULL DEFAULT now()
);

-- One evaluation per evaluator per evaluatee per session (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_tryout_evaluation') THEN
        ALTER TABLE public.tryout_evaluations
            ADD CONSTRAINT uq_tryout_evaluation
            UNIQUE (session_id, evaluatee_user_id, evaluator_user_id);
    END IF;
END $$;

-- CHECK: notes max 1000 chars (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_tryout_eval_notes_len') THEN
        ALTER TABLE public.tryout_evaluations
            ADD CONSTRAINT chk_tryout_eval_notes_len
            CHECK (notes IS NULL OR length(notes) <= 1000);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_tryout_evaluations_session
    ON public.tryout_evaluations (session_id);

CREATE INDEX IF NOT EXISTS idx_tryout_evaluations_evaluatee
    ON public.tryout_evaluations (evaluatee_user_id);

ALTER TABLE public.tryout_evaluations ENABLE ROW LEVEL SECURITY;

-- CONFIDENTIAL: only team staff (captain/manager of the session's team) can access evaluations.
-- Evaluatees cannot see their own evaluations.
DROP POLICY IF EXISTS tryout_evaluations_staff ON public.tryout_evaluations;
CREATE POLICY tryout_evaluations_staff
    ON public.tryout_evaluations
    FOR ALL
    TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.tryout_sessions ts
            JOIN public.team_members tm ON tm.team_id = ts.team_id
            WHERE ts.id = tryout_evaluations.session_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    )
    WITH CHECK (
        EXISTS (
            SELECT 1 FROM public.tryout_sessions ts
            JOIN public.team_members tm ON tm.team_id = ts.team_id
            WHERE ts.id = tryout_evaluations.session_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    );

-- Service role: unrestricted.
DROP POLICY IF EXISTS tryout_evaluations_service ON public.tryout_evaluations;
CREATE POLICY tryout_evaluations_service
    ON public.tryout_evaluations
    FOR ALL
    TO service_role
    USING (true)
    WITH CHECK (true);

-- ── Feature flags (Phase 3) ────────────────────────────────────────────────────

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_schema = 'public' AND table_name = 'feature_flags'
    ) THEN
        INSERT INTO public.feature_flags (key, name, description, flag_type, default_value, is_enabled)
        VALUES
            (
                'tryouts',
                'Tryout Sessions',
                'Esportra Competitive Phase 3: structured tryout sessions, participant management and confidential evaluations. Default off for beta.',
                'boolean',
                'false'::jsonb,
                FALSE
            )
        ON CONFLICT (key) DO NOTHING;
    END IF;
END $$;
