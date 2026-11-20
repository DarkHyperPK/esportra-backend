-- Private bucket for Air game packs: a game's official pictures (PUBG MOBILE's partner kit) plus one
-- manifest.json per game. No storage policies for anon or authenticated: only the backend (service key)
-- reads the manifest, and Air downloads files through short-lived signed URLs.
-- Guarded: the storage schema is Supabase-only and absent in local dev Postgres.
DO $$
BEGIN
  IF EXISTS (
    SELECT 1 FROM information_schema.tables
    WHERE table_schema = 'storage' AND table_name = 'buckets'
  ) THEN
    INSERT INTO storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
    VALUES ('air.game-packs', 'air.game-packs', false, 67108864,
            ARRAY['image/jpeg', 'image/png', 'image/webp', 'application/json'])
    ON CONFLICT (id) DO UPDATE SET public = false;
  END IF;
END $$;
