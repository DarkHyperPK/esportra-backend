-- Track how many times a user has released an avatar.
-- Capped at 2 to prevent monopoly cycling.
ALTER TABLE profiles
    ADD COLUMN IF NOT EXISTS avatar_release_count int NOT NULL DEFAULT 0;
