-- Ensure one Riot account can only be linked to one profile.
-- Clean up empty strings first, then add a partial unique index.

UPDATE profiles SET riot_tag = NULL WHERE riot_tag = '';

CREATE UNIQUE INDEX IF NOT EXISTS idx_profiles_riot_tag_unique
    ON profiles (riot_tag)
    WHERE riot_tag IS NOT NULL AND riot_tag != '';
