-- Private bucket for report proof screenshots. No storage policies are created for anon or authenticated:
-- only the backend (service key) uploads, and admins see images through short-lived signed URLs.
-- Guarded: the storage schema is Supabase-only and absent in local dev Postgres.
DO $$
BEGIN
  IF EXISTS (
    SELECT 1 FROM information_schema.tables
    WHERE table_schema = 'storage' AND table_name = 'buckets'
  ) THEN
    INSERT INTO storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
    VALUES ('squad-up.report-proof', 'squad-up.report-proof', false, 10485760,
            ARRAY['image/jpeg', 'image/png', 'image/webp'])
    ON CONFLICT (id) DO UPDATE SET public = false;
  END IF;
END $$;
