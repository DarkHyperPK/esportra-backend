-- Enable competitive feature flags so they appear in /api/features/status
-- and the frontend gates (competitive_profiles, lfg, recruitment, tryouts,
-- scrims, scouting) evaluate to true in production.
-- Rows may already exist as disabled from earlier migrations — use
-- DO UPDATE to flip them on. Missing rows are inserted.

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_schema = 'public' AND table_name = 'feature_flags'
    ) THEN
        INSERT INTO public.feature_flags (key, name, description, flag_type, default_value, is_enabled)
        VALUES
            ('competitive_profiles', 'Competitive Profiles',
             'Per-game competitive identity: rank, roles, stats, bio.',
             'boolean', 'true'::jsonb, TRUE),
            ('lfg', 'Looking for Group',
             'LFG listing board — post and browse looking-for-group cards by game.',
             'boolean', 'true'::jsonb, TRUE),
            ('recruitment', 'Team Recruitment',
             'Team recruitment openings and applications.',
             'boolean', 'true'::jsonb, TRUE),
            ('tryouts', 'Team Tryouts',
             'Structured tryout sessions with invitations and evaluations.',
             'boolean', 'true'::jsonb, TRUE),
            ('scrims', 'Scrim Finder',
             'Team-vs-team scrim listings and match scheduling.',
             'boolean', 'true'::jsonb, TRUE),
            ('scouting', 'Scouting & Shortlists',
             'Player scouting shortlists and notes for team managers.',
             'boolean', 'true'::jsonb, TRUE)
        ON CONFLICT (key) DO UPDATE SET is_enabled = TRUE;
    END IF;
END $$;
