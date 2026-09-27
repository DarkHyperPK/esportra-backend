-- Cascade game catalog version deactivation to tournament templates
-- When a game catalog version is deactivated, automatically deactivate all templates for games in that version

CREATE OR REPLACE FUNCTION fn_deactivate_templates_on_catalog_version_deactivation()
RETURNS TRIGGER AS $$
BEGIN
  IF OLD.is_active = true AND NEW.is_active = false THEN
    UPDATE tournament_templates
    SET is_active = false
    WHERE game_catalog_id IN (
      SELECT id FROM game_catalog_games WHERE version_id = NEW.id
    );
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_deactivate_templates_on_catalog_version ON game_catalog_versions;

CREATE TRIGGER trg_deactivate_templates_on_catalog_version
  AFTER UPDATE ON game_catalog_versions
  FOR EACH ROW
  EXECUTE FUNCTION fn_deactivate_templates_on_catalog_version_deactivation();
