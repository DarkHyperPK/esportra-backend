ALTER TABLE teams ADD COLUMN IF NOT EXISTS slug TEXT;

-- PRE-FLIGHT: Run on production before deploying, confirm expected count before proceeding:
-- SELECT COUNT(*) FROM teams WHERE slug IS NULL;
-- Expected: number of teams currently in the table (all should be populated by this migration)
UPDATE teams
SET slug = LOWER(
    REGEXP_REPLACE(
        REGEXP_REPLACE(TRIM(name), '[^a-zA-Z0-9 -]', '', 'g'),
        ' +', '-', 'g'
    )
)
WHERE slug IS NULL;

-- Handle duplicate slugs: keep the oldest team's clean slug, append 8-char id to later ones
WITH dupes AS (
    SELECT id,
           slug,
           ROW_NUMBER() OVER (PARTITION BY slug ORDER BY created_at, id) AS rn
    FROM teams
    WHERE slug IS NOT NULL
)
UPDATE teams t
SET slug = d.slug || '-' || LEFT(t.id::text, 8)
FROM dupes d
WHERE d.id = t.id AND d.rn > 1;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'teams_slug_key') THEN
    ALTER TABLE teams ADD CONSTRAINT teams_slug_key UNIQUE (slug);
  END IF;
END $$;
