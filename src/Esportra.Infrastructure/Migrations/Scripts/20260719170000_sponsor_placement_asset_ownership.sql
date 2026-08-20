-- Server-owned creative assets referenced by placement assignments.
CREATE TABLE IF NOT EXISTS public.sponsor_placement_assets (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    bucket TEXT NOT NULL CHECK (bucket = 'system.assets.partners'),
    object_path TEXT NOT NULL UNIQUE,
    public_url TEXT NOT NULL,
    placement_zone TEXT NOT NULL CHECK (placement_zone IN (
        'homepage_ticker', 'partner_showcase', 'sidebar_partner',
        'wide_partner', 'card_badge', 'partner_logo'
    )),
    asset_role TEXT NOT NULL CHECK (asset_role IN ('banner', 'logo')),
    mime_type TEXT NOT NULL CHECK (mime_type IN ('image/jpeg', 'image/png', 'image/webp')),
    width INTEGER NOT NULL CHECK (width > 0),
    height INTEGER NOT NULL CHECK (height > 0),
    byte_size BIGINT NOT NULL CHECK (byte_size > 0),
    uploaded_by UUID NOT NULL REFERENCES public.profiles(id),
    claimed_at TIMESTAMPTZ,
    deleting_at TIMESTAMPTZ,
    expires_at TIMESTAMPTZ NOT NULL DEFAULT (NOW() + INTERVAL '24 hours'),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE public.sponsor_placements
    ADD COLUMN IF NOT EXISTS banner_asset_id UUID REFERENCES public.sponsor_placement_assets(id),
    ADD COLUMN IF NOT EXISTS logo_asset_id UUID REFERENCES public.sponsor_placement_assets(id);

ALTER TABLE public.sponsor_asset_cleanup_jobs
    ADD COLUMN IF NOT EXISTS asset_id UUID REFERENCES public.sponsor_placement_assets(id),
    ADD COLUMN IF NOT EXISTS lease_owner TEXT,
    ADD COLUMN IF NOT EXISTS lease_expires_at TIMESTAMPTZ;

CREATE INDEX IF NOT EXISTS sponsor_asset_cleanup_lease_idx
    ON public.sponsor_asset_cleanup_jobs (next_attempt_at, lease_expires_at)
    WHERE completed_at IS NULL AND failed_at IS NULL;

CREATE INDEX IF NOT EXISTS sponsor_placement_assets_expiry_idx
    ON public.sponsor_placement_assets (expires_at)
    WHERE claimed_at IS NULL;

ALTER TABLE public.sponsor_placement_assets ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.sponsor_placement_assets FORCE ROW LEVEL SECURITY;
REVOKE ALL ON public.sponsor_placement_assets FROM anon, authenticated;
DROP POLICY IF EXISTS sponsor_placement_assets_service_role_all ON public.sponsor_placement_assets;
CREATE POLICY sponsor_placement_assets_service_role_all ON public.sponsor_placement_assets
    FOR ALL TO service_role USING (true) WITH CHECK (true);
GRANT ALL ON public.sponsor_placement_assets TO service_role;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'sponsor_placements_copy_length_check') THEN
        ALTER TABLE public.sponsor_placements ADD CONSTRAINT sponsor_placements_copy_length_check CHECK (
            length(COALESCE(headline, '')) <= 120
            AND length(COALESCE(cta_text, '')) <= 60
            AND length(COALESCE(cta_url, '')) <= 2048
            AND length(COALESCE(banner_url, '')) <= 2048
            AND length(COALESCE(logo_url, '')) <= 2048
        );
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'sponsor_placement_assets_path_length_check') THEN
        ALTER TABLE public.sponsor_placement_assets ADD CONSTRAINT sponsor_placement_assets_path_length_check CHECK (
            length(object_path) <= 512 AND length(public_url) <= 2048
        );
    END IF;
END $$;
