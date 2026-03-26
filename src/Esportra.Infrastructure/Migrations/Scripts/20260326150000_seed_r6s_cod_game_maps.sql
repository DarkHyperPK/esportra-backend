-- Seed game_maps for Rainbow Six Siege and Call of Duty
-- Valorant and CS2 maps already exist in the table.

-- ═══════════════════════════════════════════════════════════════════
-- Rainbow Six Siege — competitive map pool (9 maps)
-- ═══════════════════════════════════════════════════════════════════
INSERT INTO game_maps (game, map_name, is_active)
VALUES
  ('Rainbow Six Siege', 'Bank',              true),
  ('Rainbow Six Siege', 'Border',            true),
  ('Rainbow Six Siege', 'Chalet',            true),
  ('Rainbow Six Siege', 'Clubhouse',         true),
  ('Rainbow Six Siege', 'Consulate',         true),
  ('Rainbow Six Siege', 'Kafe Dostoyevsky',  true),
  ('Rainbow Six Siege', 'Oregon',            true),
  ('Rainbow Six Siege', 'Skyscraper',        true),
  ('Rainbow Six Siege', 'Theme Park',        true)
ON CONFLICT DO NOTHING;

-- ═══════════════════════════════════════════════════════════════════
-- Call of Duty — Black Ops 6 competitive map pool (10 maps)
-- Split by game mode: HP = Hardpoint, SnD = Search & Destroy, CTL = Control
-- ═══════════════════════════════════════════════════════════════════
INSERT INTO game_maps (game, map_name, is_active)
VALUES
  ('Call of Duty', 'Hacienda',     true),
  ('Call of Duty', 'Protocol',     true),
  ('Call of Duty', 'Rewind',       true),
  ('Call of Duty', 'Skyline',      true),
  ('Call of Duty', 'Subsonic',     true),
  ('Call of Duty', 'Vault',        true),
  ('Call of Duty', 'Dealership',   true),
  ('Call of Duty', 'Red Card',     true),
  ('Call of Duty', 'Pit',          true),
  ('Call of Duty', 'Warhead',      true)
ON CONFLICT DO NOTHING;
