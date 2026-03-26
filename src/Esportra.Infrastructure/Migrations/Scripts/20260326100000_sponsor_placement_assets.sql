-- Add placement_assets jsonb column to sponsors table
-- Maps zone key → asset URL for admin-controlled per-zone asset selection
-- Example: {"homepage_banner": "https://...banner.png", "global_ticker": "https://...logo.png"}
ALTER TABLE sponsors ADD COLUMN IF NOT EXISTS placement_assets jsonb DEFAULT '{}'::jsonb;

COMMENT ON COLUMN sponsors.placement_assets IS 'Per-zone asset overrides: { "zone_key": "asset_url" }';
