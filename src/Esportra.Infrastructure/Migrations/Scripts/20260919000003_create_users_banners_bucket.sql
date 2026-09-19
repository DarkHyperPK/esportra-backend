-- Create users.banners storage bucket for profile banner images.
-- Public bucket; owners can upload and delete their own files.
-- Guarded: skips silently on non-Supabase environments where storage.buckets is absent.
-- Uses EXECUTE for storage DDL to avoid parse-time failures on plain Postgres (CI replay).

DO $$
BEGIN
    -- storage.buckets only exists in Supabase — skip in plain Postgres (CI replay, local dev)
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_schema = 'storage' AND table_name = 'buckets'
    ) THEN
        RETURN;
    END IF;

    -- Create bucket (idempotent)
    EXECUTE $sql$
        INSERT INTO storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
        VALUES (
            'users.banners',
            'users.banners',
            true,
            10485760,
            ARRAY['image/jpeg','image/png','image/webp','image/avif']
        )
        ON CONFLICT (id) DO NOTHING
    $sql$;

    -- Policy: public SELECT
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'user_banners_public_select'
    ) THEN
        EXECUTE $sql$
            CREATE POLICY user_banners_public_select
                ON storage.objects FOR SELECT
                TO public
                USING (bucket_id = 'users.banners')
        $sql$;
    END IF;

    -- Policy: authenticated INSERT
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'user_banners_auth_insert'
    ) THEN
        EXECUTE $sql$
            CREATE POLICY user_banners_auth_insert
                ON storage.objects FOR INSERT
                TO authenticated
                WITH CHECK (bucket_id = 'users.banners')
        $sql$;
    END IF;

    -- Policy: authenticated DELETE by owner
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'user_banners_auth_delete'
    ) THEN
        EXECUTE $sql$
            CREATE POLICY user_banners_auth_delete
                ON storage.objects FOR DELETE
                TO authenticated
                USING (bucket_id = 'users.banners' AND owner = auth.uid())
        $sql$;
    END IF;
END;
$$;
