-- Add discord_dm_enabled to profiles.settings JSONB field.
-- No column changes needed — settings is already a JSONB column.
-- This migration just adds an API endpoint comment and ensures the column exists.

-- Ensure settings column exists on profiles (idempotent)
DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'profiles' AND column_name = 'settings'
    ) THEN
        ALTER TABLE public.profiles ADD COLUMN settings jsonb DEFAULT '{}'::jsonb;
    END IF;
END $$;

-- Backfill null settings to empty JSON object
UPDATE public.profiles SET settings = '{}'::jsonb WHERE settings IS NULL;
