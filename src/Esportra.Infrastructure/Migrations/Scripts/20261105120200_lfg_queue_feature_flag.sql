-- Queue Now feature flag. Default OFF (dark launch); an existing row is never overwritten.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_schema = 'public' AND table_name = 'feature_flags'
    ) THEN
        INSERT INTO public.feature_flags (key, name, description, flag_type, default_value, is_enabled)
        VALUES
            ('lfg_queue', 'LFG Queue Now',
             'Live swipe-style LFG matching (Valorant). Off blocks /api/competitive/lfg/swipe routes.',
             'boolean', 'false'::jsonb, FALSE)
        ON CONFLICT (key) DO NOTHING;
    END IF;
END $$;
