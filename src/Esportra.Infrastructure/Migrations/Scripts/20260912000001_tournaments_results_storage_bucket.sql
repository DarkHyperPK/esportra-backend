-- Create storage bucket for tournament result screenshots and media.
-- Uses ON CONFLICT to be idempotent (safe to run if bucket already exists).

INSERT INTO storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
VALUES
    ('tournaments.results',
     'tournaments.results',
     true,
     10485760,  -- 10 MB
     ARRAY['image/jpeg','image/png','image/webp','image/gif','image/jpg'])
ON CONFLICT (id) DO NOTHING;

-- ── tournaments.results policies ──────────────────────────────────────────────

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'tournaments_results_public_select'
    ) THEN
        CREATE POLICY tournaments_results_public_select
            ON storage.objects FOR SELECT
            TO public
            USING (bucket_id = 'tournaments.results');
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'tournaments_results_auth_insert'
    ) THEN
        CREATE POLICY tournaments_results_auth_insert
            ON storage.objects FOR INSERT
            TO authenticated
            WITH CHECK (bucket_id = 'tournaments.results');
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'tournaments_results_auth_delete'
    ) THEN
        CREATE POLICY tournaments_results_auth_delete
            ON storage.objects FOR DELETE
            TO authenticated
            USING (bucket_id = 'tournaments.results' AND owner = auth.uid()::text);
    END IF;
END;
$$;
