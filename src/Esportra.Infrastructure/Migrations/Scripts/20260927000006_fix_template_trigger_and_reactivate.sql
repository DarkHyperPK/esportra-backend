-- Fix trigger + re-activate deactivated tournament templates.
--
-- Root cause: fn_deactivate_templates_on_catalog_version_deactivation only
-- handled the true→false direction. ImportPackagedCatalogAsync deactivates ALL
-- versions first (trigger fires, templates set is_active=false), then
-- re-activates the target version (trigger did NOT fire — templates stayed
-- false). ON CONFLICT DO NOTHING in the seed meant they were never corrected.
--
-- Fix:
--   1. Replace trigger function to also re-activate templates on false→true.
--   2. Re-activate all templates whose game references the currently active
--      catalog version (repairs existing stale rows).

CREATE OR REPLACE FUNCTION fn_deactivate_templates_on_catalog_version_deactivation()
RETURNS TRIGGER AS $$
BEGIN
  IF OLD.is_active = true AND NEW.is_active = false THEN
    UPDATE tournament_templates
    SET is_active = false
    WHERE game_catalog_id IN (
      SELECT id FROM game_catalog_games WHERE version_id = NEW.id
    );
  ELSIF OLD.is_active = false AND NEW.is_active = true THEN
    UPDATE tournament_templates
    SET is_active = true
    WHERE game_catalog_id IN (
      SELECT id FROM game_catalog_games WHERE version_id = NEW.id
    );
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Re-activate any templates whose game already belongs to the active version
-- (repairs rows deactivated by the broken trigger on previous deploys).
UPDATE tournament_templates
SET is_active = true
WHERE is_active = false
  AND game_catalog_id IN (
    SELECT g.id
    FROM game_catalog_games g
    INNER JOIN game_catalog_versions v ON g.version_id = v.id
    WHERE v.is_active = true AND v.status = 'active'
  );
