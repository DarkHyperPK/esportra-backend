-- Restore full Rainbow Six Siege map catalog and extend veto action history.

-- ═══════════════════════════════════════════════════════════════════
-- Rainbow Six Siege — full competitive map manifest (27 maps)
-- ═══════════════════════════════════════════════════════════════════
INSERT INTO public.game_maps (game, map_name, is_active)
VALUES
  ('Rainbow Six Siege', 'Bank',              true),
  ('Rainbow Six Siege', 'Border',            true),
  ('Rainbow Six Siege', 'Calypso Casino',    true),
  ('Rainbow Six Siege', 'Chalet',            true),
  ('Rainbow Six Siege', 'Close Quarters',    true),
  ('Rainbow Six Siege', 'Clubhouse',         true),
  ('Rainbow Six Siege', 'Coastline',         true),
  ('Rainbow Six Siege', 'Consulate',         true),
  ('Rainbow Six Siege', 'Emerald Plains',    true),
  ('Rainbow Six Siege', 'Favela',            true),
  ('Rainbow Six Siege', 'Fortress',          true),
  ('Rainbow Six Siege', 'Hereford',          true),
  ('Rainbow Six Siege', 'House',             true),
  ('Rainbow Six Siege', 'Kanal',             true),
  ('Rainbow Six Siege', 'Lair',              true),
  ('Rainbow Six Siege', 'Nighthaven Labs',   true),
  ('Rainbow Six Siege', 'Oregon',            true),
  ('Rainbow Six Siege', 'Outback',           true),
  ('Rainbow Six Siege', 'Plane',             true),
  ('Rainbow Six Siege', 'Kafe Dostoyevsky',  true),
  ('Rainbow Six Siege', 'Skyscraper',        true),
  ('Rainbow Six Siege', 'Stadium Alpha',     true),
  ('Rainbow Six Siege', 'Stadium Bravo',     true),
  ('Rainbow Six Siege', 'Theme Park',        true),
  ('Rainbow Six Siege', 'Tower',             true),
  ('Rainbow Six Siege', 'Villa',             true),
  ('Rainbow Six Siege', 'Yacht',             true)
ON CONFLICT (game, map_name) DO UPDATE SET is_active = true;

-- ═══════════════════════════════════════════════════════════════════
-- match_map_veto_actions — history columns + indexes
-- ═══════════════════════════════════════════════════════════════════
ALTER TABLE public.match_map_veto_actions
  ADD COLUMN IF NOT EXISTS match_id       UUID,
  ADD COLUMN IF NOT EXISTS tournament_id  UUID,
  ADD COLUMN IF NOT EXISTS team_id        UUID,
  ADD COLUMN IF NOT EXISTS team_side      TEXT,
  ADD COLUMN IF NOT EXISTS action_type    TEXT,
  ADD COLUMN IF NOT EXISTS map_id         UUID,
  ADD COLUMN IF NOT EXISTS action_number  INTEGER,
  ADD COLUMN IF NOT EXISTS side           TEXT,
  ADD COLUMN IF NOT EXISTS created_by     UUID,
  ADD COLUMN IF NOT EXISTS created_at     TIMESTAMPTZ DEFAULT now();

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'match_map_veto_actions_veto_id_action_number_key'
  ) THEN
    ALTER TABLE public.match_map_veto_actions
      ADD CONSTRAINT match_map_veto_actions_veto_id_action_number_key
      UNIQUE (veto_id, action_number);
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_match_map_veto_actions_match_id
  ON public.match_map_veto_actions (match_id);

CREATE INDEX IF NOT EXISTS idx_match_map_veto_actions_veto_id
  ON public.match_map_veto_actions (veto_id);
