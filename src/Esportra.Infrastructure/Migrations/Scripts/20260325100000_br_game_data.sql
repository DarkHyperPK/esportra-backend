-- Battle Royale game data persistence
-- Stores lobby codes, game status, results, and player evidence per tournament
CREATE TABLE IF NOT EXISTS br_game_data (
  tournament_id TEXT PRIMARY KEY,
  games JSONB NOT NULL DEFAULT '{}',
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_by UUID REFERENCES auth.users(id)
);

ALTER TABLE br_game_data ENABLE ROW LEVEL SECURITY;

-- Any authenticated user can read BR game data (players need lobby codes)
CREATE POLICY "br_game_data_select" ON br_game_data
  FOR SELECT TO authenticated USING (true);

-- Any authenticated user can insert (organizer check enforced at app layer)
CREATE POLICY "br_game_data_insert" ON br_game_data
  FOR INSERT TO authenticated WITH CHECK (true);

-- Any authenticated user can update (organizer check enforced at app layer)
CREATE POLICY "br_game_data_update" ON br_game_data
  FOR UPDATE TO authenticated USING (true) WITH CHECK (true);
