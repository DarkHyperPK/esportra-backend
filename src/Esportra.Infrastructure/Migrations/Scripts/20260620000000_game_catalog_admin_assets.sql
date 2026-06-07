-- Game catalog admin workflow + asset columns.
-- Additive: existing packaged/active versions remain valid.

ALTER TABLE public.game_catalog_versions
    ADD COLUMN IF NOT EXISTS source TEXT NOT NULL DEFAULT 'packaged',
    ADD COLUMN IF NOT EXISTS created_by UUID,
    ADD COLUMN IF NOT EXISTS published_by UUID,
    ADD COLUMN IF NOT EXISTS published_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS notes TEXT;

ALTER TABLE public.game_catalog_versions
    DROP CONSTRAINT IF EXISTS chk_game_catalog_versions_status;

ALTER TABLE public.game_catalog_versions
    ADD CONSTRAINT chk_game_catalog_versions_status
        CHECK (status IN ('pending','active','failed','draft'));

ALTER TABLE public.game_catalog_versions
    DROP CONSTRAINT IF EXISTS chk_game_catalog_versions_source;

ALTER TABLE public.game_catalog_versions
    ADD CONSTRAINT chk_game_catalog_versions_source
        CHECK (source IN ('packaged','admin'));

CREATE UNIQUE INDEX IF NOT EXISTS ux_game_catalog_versions_single_draft
    ON public.game_catalog_versions(status)
    WHERE status = 'draft';

ALTER TABLE public.game_catalog_games
    ADD COLUMN IF NOT EXISTS logo_url TEXT,
    ADD COLUMN IF NOT EXISTS icon_url TEXT,
    ADD COLUMN IF NOT EXISTS cover_url TEXT,
    ADD COLUMN IF NOT EXISTS sort_order INT NOT NULL DEFAULT 0;

-- Public game-assets bucket for catalog logos/icons/covers.
INSERT INTO storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
VALUES
    ('game-assets',
     'game-assets',
     true,
     5242880,
     ARRAY['image/jpeg','image/png','image/webp','image/gif','image/jpg','image/svg+xml'])
ON CONFLICT (id) DO NOTHING;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'game_assets_public_select'
    ) THEN
        CREATE POLICY game_assets_public_select
            ON storage.objects FOR SELECT
            TO public
            USING (bucket_id = 'game-assets');
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'game_assets_service_insert'
    ) THEN
        CREATE POLICY game_assets_service_insert
            ON storage.objects FOR INSERT
            TO service_role
            WITH CHECK (bucket_id = 'game-assets');
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'game_assets_service_delete'
    ) THEN
        CREATE POLICY game_assets_service_delete
            ON storage.objects FOR DELETE
            TO service_role
            USING (bucket_id = 'game-assets');
    END IF;
END;
$$;
