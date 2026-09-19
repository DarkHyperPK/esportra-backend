-- achievements: catalogue of earnable achievements.
-- Rows are platform-defined; users cannot insert directly.

CREATE TABLE IF NOT EXISTS achievements (
    id          UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    key         TEXT        NOT NULL UNIQUE,
    name        TEXT        NOT NULL,
    description TEXT,
    icon_url    TEXT,
    points      INT         NOT NULL DEFAULT 0,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- RLS
ALTER TABLE achievements ENABLE ROW LEVEL SECURITY;

-- Public read: achievement definitions are visible to everyone
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'achievements' AND policyname = 'achievements_public_select'
  ) THEN
    CREATE POLICY "achievements_public_select" ON achievements FOR SELECT USING (true);
  END IF;
END $$;

-- Seed placeholder achievement; no-op if it already exists
INSERT INTO achievements (key, name, description, points)
VALUES ('first_tournament', 'First Blood', 'Entered your first tournament', 10)
ON CONFLICT (key) DO NOTHING;
