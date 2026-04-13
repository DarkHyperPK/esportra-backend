-- ============================================================================
-- Migration: 20260412183800_extend_broadcast_themes.sql
-- Purpose:   Extend broadcast_themes with overlay-specific columns for the
--            Esportra Broadcaster desktop app.
-- ============================================================================

ALTER TABLE broadcast_themes ADD COLUMN IF NOT EXISTS overlay_opacity  DECIMAL(3,2) DEFAULT 1.0;
ALTER TABLE broadcast_themes ADD COLUMN IF NOT EXISTS animation_speed  TEXT DEFAULT 'normal';
ALTER TABLE broadcast_themes ADD COLUMN IF NOT EXISTS border_style     TEXT DEFAULT 'solid';
ALTER TABLE broadcast_themes ADD COLUMN IF NOT EXISTS border_radius    INT DEFAULT 8;
ALTER TABLE broadcast_themes ADD COLUMN IF NOT EXISTS updated_at       TIMESTAMPTZ DEFAULT now();

-- Add CHECK constraint for animation_speed if not already present
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.check_constraints
    WHERE constraint_name = 'broadcast_themes_animation_speed_check'
  ) THEN
    ALTER TABLE broadcast_themes
      ADD CONSTRAINT broadcast_themes_animation_speed_check
      CHECK (animation_speed IN ('slow', 'normal', 'fast', 'none'));
  END IF;
END $$;
