-- Avatar pool: a finite set of claimable (style, seed) combos.
-- Each combo can be owned by exactly one user, and each user can own at most one.

CREATE TABLE IF NOT EXISTS avatar_pool (
    id          uuid        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    style       text        NOT NULL,
    seed        text        NOT NULL,
    claimed_by  uuid        REFERENCES profiles(id) ON DELETE SET NULL,
    claimed_at  timestamptz,
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- One (style, seed) pair can only exist once in the pool
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'avatar_pool_style_seed_unique') THEN
    ALTER TABLE avatar_pool ADD CONSTRAINT avatar_pool_style_seed_unique UNIQUE (style, seed);
  END IF;
END $$;

-- One user can own at most one avatar at a time
CREATE UNIQUE INDEX IF NOT EXISTS avatar_pool_claimed_by_unique
    ON avatar_pool (claimed_by)
    WHERE claimed_by IS NOT NULL;

-- RLS
ALTER TABLE avatar_pool ENABLE ROW LEVEL SECURITY;

-- Anyone can read the pool (to browse available avatars)
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'avatar_pool' AND policyname = 'avatar_pool_select') THEN
    CREATE POLICY avatar_pool_select ON avatar_pool FOR SELECT USING (true);
  END IF;
END $$;
