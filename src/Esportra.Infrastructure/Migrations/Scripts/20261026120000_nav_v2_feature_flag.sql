-- Seed nav_v2 feature flag as enabled (default on).
-- The sidebar navigation shell (ProductSidebar) is gated behind this flag.
-- Without it the /api/features/status endpoint never returns nav_v2 and the
-- frontend always falls back to the legacy top bar.

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_schema = 'public' AND table_name = 'feature_flags'
    ) THEN
        INSERT INTO public.feature_flags (key, name, description, flag_type, default_value, is_enabled)
        VALUES (
            'nav_v2',
            'Sidebar Navigation',
            'New persistent sidebar navigation shell (nav_v2). Replaces the top bar on desktop. Off falls back to the legacy top bar. Default on.',
            'boolean',
            'true'::jsonb,
            TRUE
        )
        ON CONFLICT (key) DO NOTHING;
    END IF;
END $$;
