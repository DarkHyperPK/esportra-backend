-- Queue Now is now "Squad Up" (it is not a ranked queue; ranked scrim matchmaking comes later).
-- Rename the flag key in place so its id, state, rules and overrides are preserved.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_schema = 'public' AND table_name = 'feature_flags'
    ) THEN
        RETURN;
    END IF;

    IF EXISTS (SELECT 1 FROM public.feature_flags WHERE key = 'lfg_queue')
       AND NOT EXISTS (SELECT 1 FROM public.feature_flags WHERE key = 'squad_up') THEN
        UPDATE public.feature_flags
        SET key = 'squad_up', name = 'Squad Up',
            description = 'Live swipe-style teammate finder (Valorant). Off blocks /api/competitive/lfg/swipe routes.'
        WHERE key = 'lfg_queue';
    ELSE
        INSERT INTO public.feature_flags (key, name, description, flag_type, default_value, is_enabled)
        VALUES ('squad_up', 'Squad Up',
                'Live swipe-style teammate finder (Valorant). Off blocks /api/competitive/lfg/swipe routes.',
                'boolean', 'false'::jsonb, FALSE)
        ON CONFLICT (key) DO NOTHING;
    END IF;
END $$;
