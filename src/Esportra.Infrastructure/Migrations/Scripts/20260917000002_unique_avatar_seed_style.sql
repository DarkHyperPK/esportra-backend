-- Enforce one owner per (seed, style) combination. Partial index excludes rows
-- with no seed set so null/default profiles don't conflict with each other.
CREATE UNIQUE INDEX IF NOT EXISTS profiles_avatar_seed_style_unique
    ON profiles (avatar_seed, avatar_style)
    WHERE avatar_seed IS NOT NULL;
