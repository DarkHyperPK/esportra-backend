-- PROJ-070 Phase 7: Missing FK backing indexes on competitive tables.
-- Every REFERENCES column must have a supporting index for FK enforcement
-- and JOIN/lookup performance. All statements are idempotent via IF NOT EXISTS.

-- recruitment_openings
CREATE INDEX IF NOT EXISTS idx_recruitment_openings_tournament_id
    ON public.recruitment_openings (tournament_id)
    WHERE tournament_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_recruitment_openings_created_by
    ON public.recruitment_openings (created_by);

-- recruitment_application_events
CREATE INDEX IF NOT EXISTS idx_recruitment_app_events_actor
    ON public.recruitment_application_events (actor_user_id);

-- tryout_sessions
CREATE INDEX IF NOT EXISTS idx_tryout_sessions_created_by
    ON public.tryout_sessions (created_by);

-- tryout_participants
CREATE INDEX IF NOT EXISTS idx_tryout_participants_invited_by
    ON public.tryout_participants (invited_by);

-- tryout_evaluations
CREATE INDEX IF NOT EXISTS idx_tryout_evaluations_evaluator
    ON public.tryout_evaluations (evaluator_user_id);

-- scrim_result_reports
CREATE INDEX IF NOT EXISTS idx_scrim_result_reports_reporting_team
    ON public.scrim_result_reports (reporting_team_id);

-- scrim_disputes
CREATE INDEX IF NOT EXISTS idx_scrim_disputes_raised_by_team
    ON public.scrim_disputes (raised_by_team_id);

-- practice_sessions
CREATE INDEX IF NOT EXISTS idx_practice_sessions_created_by
    ON public.practice_sessions (created_by);
