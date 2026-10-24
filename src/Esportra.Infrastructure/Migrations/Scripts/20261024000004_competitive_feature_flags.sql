-- PROJ-065 Competitive Phase 1: feature flag seeds
-- Both flags default OFF (beta); existing rows are never overwritten.

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.tables
        WHERE table_schema = 'public'
          AND table_name   = 'feature_flags'
    ) THEN
        INSERT INTO public.feature_flags (key, name, description, flag_type, default_value, is_enabled)
        VALUES
            (
                'competitive_profiles',
                'Competitive Profiles',
                'Esportra Competitive Phase 1: player game profiles, rank, roles, region, LFT status and availability. Off hides all competitive surfaces. Default off for beta.',
                'boolean',
                'false'::jsonb,
                FALSE
            ),
            (
                'lfg',
                'LFG Listings & Discovery',
                'Esportra Competitive Phase 1: LFG listing creation, discovery search and interest flow. Off hides LFG surfaces. Default off for beta.',
                'boolean',
                'false'::jsonb,
                FALSE
            )
        ON CONFLICT (key) DO NOTHING;
    END IF;
END $$;
