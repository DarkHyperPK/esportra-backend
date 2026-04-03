-- Broadcast themes for HUD branding
-- Migration: 20260404080000_broadcast_themes.sql

CREATE TABLE IF NOT EXISTS broadcast_themes (
  id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_id         UUID REFERENCES auth.users(id) ON DELETE CASCADE,
  name             TEXT NOT NULL,
  primary_color    TEXT DEFAULT '#E11D48',
  secondary_color  TEXT DEFAULT '#0F0F0F',
  accent_color     TEXT DEFAULT '#FFFFFF',
  font_family      TEXT DEFAULT 'Inter',
  logo_url         TEXT,
  background_url   TEXT,
  custom_css       TEXT,
  is_default       BOOLEAN DEFAULT false,
  created_at       TIMESTAMPTZ DEFAULT NOW()
);

ALTER TABLE broadcast_themes ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'broadcast_themes_service_all' AND tablename = 'broadcast_themes') THEN
    CREATE POLICY broadcast_themes_service_all ON broadcast_themes
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'broadcast_themes_own' AND tablename = 'broadcast_themes') THEN
    CREATE POLICY broadcast_themes_own ON broadcast_themes
      FOR ALL USING (auth.uid() = owner_id);
  END IF;
END $$;
