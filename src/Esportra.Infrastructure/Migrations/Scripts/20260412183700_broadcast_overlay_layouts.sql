-- ============================================================================
-- Migration: 20260412183700_broadcast_overlay_layouts.sql
-- Purpose:   Overlay layout storage for the drag-and-drop editor.
--            Widgets serialized as JSONB array. Supports user layouts,
--            platform templates, and community-shared layouts.
-- ============================================================================

CREATE TABLE IF NOT EXISTS broadcast_overlay_layouts (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id           UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    name              TEXT NOT NULL,
    game              TEXT NOT NULL DEFAULT 'valorant',
    resolution_width  INT NOT NULL DEFAULT 1920,
    resolution_height INT NOT NULL DEFAULT 1080,
    widgets           JSONB NOT NULL DEFAULT '[]',
    is_template       BOOLEAN NOT NULL DEFAULT false,
    is_public         BOOLEAN NOT NULL DEFAULT false,
    thumbnail_url     TEXT,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);

ALTER TABLE broadcast_overlay_layouts ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'broadcast_overlay_layouts_service_all' AND tablename = 'broadcast_overlay_layouts') THEN
    CREATE POLICY broadcast_overlay_layouts_service_all ON broadcast_overlay_layouts
      FOR ALL USING (auth.role() = 'service_role');
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'broadcast_overlay_layouts_own' AND tablename = 'broadcast_overlay_layouts') THEN
    CREATE POLICY broadcast_overlay_layouts_own ON broadcast_overlay_layouts
      FOR ALL USING (auth.uid() = user_id);
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'broadcast_overlay_layouts_public_read' AND tablename = 'broadcast_overlay_layouts') THEN
    CREATE POLICY broadcast_overlay_layouts_public_read ON broadcast_overlay_layouts
      FOR SELECT USING (is_template = true OR is_public = true);
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_broadcast_overlay_layouts_user_id ON broadcast_overlay_layouts(user_id);
CREATE INDEX IF NOT EXISTS idx_broadcast_overlay_layouts_game ON broadcast_overlay_layouts(game);
CREATE INDEX IF NOT EXISTS idx_broadcast_overlay_layouts_is_template ON broadcast_overlay_layouts(is_template) WHERE is_template = true;
CREATE INDEX IF NOT EXISTS idx_broadcast_overlay_layouts_is_public ON broadcast_overlay_layouts(is_public) WHERE is_public = true;

GRANT ALL ON broadcast_overlay_layouts TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON broadcast_overlay_layouts TO authenticated;
