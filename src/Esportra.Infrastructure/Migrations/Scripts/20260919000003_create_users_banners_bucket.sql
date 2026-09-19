-- Create users.banners storage bucket for profile banner images.
-- Public bucket; owners can upload and delete their own files.

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'storage') THEN
        INSERT INTO storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
        VALUES (
            'users.banners',
            'users.banners',
            true,
            10485760,  -- 10 MB
            ARRAY['image/jpeg','image/png','image/webp','image/avif']
        )
        ON CONFLICT (id) DO NOTHING;

        IF NOT EXISTS (
            SELECT 1 FROM pg_policies
            WHERE schemaname = 'storage'
              AND tablename  = 'objects'
              AND policyname = 'user_banners_public_select'
        ) THEN
            CREATE POLICY user_banners_public_select
                ON storage.objects FOR SELECT
                TO public
                USING (bucket_id = 'users.banners');
        END IF;

        IF NOT EXISTS (
            SELECT 1 FROM pg_policies
            WHERE schemaname = 'storage'
              AND tablename  = 'objects'
              AND policyname = 'user_banners_auth_insert'
        ) THEN
            CREATE POLICY user_banners_auth_insert
                ON storage.objects FOR INSERT
                TO authenticated
                WITH CHECK (bucket_id = 'users.banners');
        END IF;

        IF NOT EXISTS (
            SELECT 1 FROM pg_policies
            WHERE schemaname = 'storage'
              AND tablename  = 'objects'
              AND policyname = 'user_banners_auth_delete'
        ) THEN
            CREATE POLICY user_banners_auth_delete
                ON storage.objects FOR DELETE
                TO authenticated
                USING (bucket_id = 'users.banners' AND owner = auth.uid());
        END IF;
    END IF;
END;
$$;
