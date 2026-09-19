-- user_achievements: records which achievements each user has earned and when.

CREATE TABLE IF NOT EXISTS user_achievements (
    user_id        UUID        NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    achievement_id UUID        NOT NULL REFERENCES achievements(id) ON DELETE CASCADE,
    earned_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (user_id, achievement_id)
);

-- Index for fast lookup of all achievements earned by a given user
CREATE INDEX IF NOT EXISTS idx_user_achievements_user_id ON user_achievements(user_id);

-- RLS
ALTER TABLE user_achievements ENABLE ROW LEVEL SECURITY;

-- Public read: earned achievements are visible to everyone
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'user_achievements' AND policyname = 'user_achievements_public_select'
  ) THEN
    CREATE POLICY "user_achievements_public_select" ON user_achievements FOR SELECT USING (true);
  END IF;
END $$;

-- Self insert: a user may only record their own achievement grants
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'user_achievements' AND policyname = 'user_achievements_self_insert'
  ) THEN
    CREATE POLICY "user_achievements_self_insert" ON user_achievements FOR INSERT WITH CHECK (auth.uid() = user_id);
  END IF;
END $$;
