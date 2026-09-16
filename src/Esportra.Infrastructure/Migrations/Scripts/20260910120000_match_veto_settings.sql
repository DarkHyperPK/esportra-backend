-- Creates the match_veto_settings table to store per-match custom veto configuration.
-- Separate from match_map_vetos (execution state) — owns the mode + step sequence only.

CREATE TABLE IF NOT EXISTS public.match_veto_settings (
    match_id    UUID PRIMARY KEY,
    mode        TEXT NOT NULL DEFAULT 'default' CHECK (mode IN ('default', 'custom')),
    sequence    JSONB DEFAULT NULL,
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_by  UUID
);

-- FK: match_id → brkt_matches(id) ON DELETE CASCADE
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'match_veto_settings_match_id_fkey'
    ) THEN
        ALTER TABLE public.match_veto_settings
            ADD CONSTRAINT match_veto_settings_match_id_fkey
            FOREIGN KEY (match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE;
    END IF;
END $$;

-- FK: updated_by → auth.users(id)
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'match_veto_settings_updated_by_fkey'
    ) THEN
        ALTER TABLE public.match_veto_settings
            ADD CONSTRAINT match_veto_settings_updated_by_fkey
            FOREIGN KEY (updated_by) REFERENCES auth.users(id);
    END IF;
END $$;

-- RLS: default deny; explicit allow below
ALTER TABLE public.match_veto_settings ENABLE ROW LEVEL SECURITY;

-- SELECT: all authenticated users (API enforces match room access above this layer)
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE policyname = 'match_veto_settings_select_authenticated'
          AND tablename = 'match_veto_settings'
    ) THEN
        CREATE POLICY "match_veto_settings_select_authenticated" ON public.match_veto_settings
            FOR SELECT
            TO authenticated
            USING (true);
    END IF;
END $$;

-- INSERT: authenticated user must be the actor recording the change (defense-in-depth;
--         production writes go through service_role which bypasses RLS)
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE policyname = 'match_veto_settings_insert_authenticated'
          AND tablename = 'match_veto_settings'
    ) THEN
        CREATE POLICY "match_veto_settings_insert_authenticated" ON public.match_veto_settings
            FOR INSERT
            TO authenticated
            WITH CHECK (auth.uid() = updated_by);
    END IF;
END $$;

-- UPDATE: authenticated user must be the actor recorded on the row
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE policyname = 'match_veto_settings_update_authenticated'
          AND tablename = 'match_veto_settings'
    ) THEN
        CREATE POLICY "match_veto_settings_update_authenticated" ON public.match_veto_settings
            FOR UPDATE
            TO authenticated
            USING (auth.uid() = updated_by)
            WITH CHECK (auth.uid() = updated_by);
    END IF;
END $$;

-- Grants
GRANT ALL ON public.match_veto_settings TO service_role;
GRANT SELECT, INSERT, UPDATE ON public.match_veto_settings TO authenticated;
