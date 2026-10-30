-- PROJ-077: Rank-filter discover indexes
--
-- Context:
--   The discover endpoint filters scrim_listings by (game_slug, status = 'open'), then
--   joins game_catalog_rank_tiers twice (min and max) to compare sort_order values.
--   Existing index: idx_scrim_listings_game_status is a partial on (game_slug) WHERE status = 'open'.
--   That partial covers the hot path for open listings but not organiser views across all statuses.
--
--   The rank join pattern is:
--     JOIN game_catalog_rank_tiers min_tier
--       ON min_tier.game_slug = sl.game_slug AND min_tier.tier_key = sl.min_rank_key
--      AND min_tier.version_id = <active_version_id>
--   The unique constraint on game_catalog_rank_tiers is (version_id, game_slug, tier_key).
--   version_id is the leading column, so when the active version is resolved via a subquery
--   the planner cannot use that constraint for a (game_slug, tier_key) lookup directly.
--   A dedicated (game_slug, tier_key) index closes this gap.
--
--   GIN is not applicable: no array intersection or full-text predicate is involved.

-- 1. Composite index on scrim_listings(game_slug, status)
--    Covers the primary discover filter across all status values.
--    Complements the existing partial index which covers only status = 'open'.
CREATE INDEX IF NOT EXISTS idx_scrim_listings_game_status_composite
    ON public.scrim_listings (game_slug, status);

-- 2. Index on game_catalog_rank_tiers(game_slug, tier_key)
--    Supports the rank join when version_id is resolved in a subquery or CTE.
--    The existing unique constraint (version_id, game_slug, tier_key) cannot serve
--    this lookup efficiently because version_id is not available as a leading equality
--    predicate during the join rewrite.
CREATE INDEX IF NOT EXISTS idx_game_catalog_rank_tiers_slug_tierkey
    ON public.game_catalog_rank_tiers (game_slug, tier_key);
