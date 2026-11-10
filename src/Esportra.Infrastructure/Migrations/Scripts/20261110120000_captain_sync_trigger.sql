-- Sync trigger: teams.captain_id is the single source of truth for captaincy.
-- When captain_id changes, this trigger automatically updates team_members.role
-- so all 30+ existing auth/notification/RLS paths see the change immediately.

CREATE OR REPLACE FUNCTION sync_captain_role_from_captain_id() RETURNS trigger AS $$
BEGIN
  IF NEW.captain_id IS DISTINCT FROM OLD.captain_id THEN
    -- Demote the previous captain (if any) to 'member'
    IF OLD.captain_id IS NOT NULL THEN
      UPDATE team_members
      SET role = 'member'
      WHERE team_id = NEW.id
        AND user_id = OLD.captain_id
        AND role = 'captain'
        AND is_active = true;
    END IF;

    -- Promote the new captain
    IF NEW.captain_id IS NOT NULL THEN
      UPDATE team_members
      SET role = 'captain'
      WHERE team_id = NEW.id
        AND user_id = NEW.captain_id
        AND is_active = true
        AND role != 'captain';
    END IF;
  END IF;

  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_sync_captain_role ON teams;
CREATE TRIGGER trg_sync_captain_role
  AFTER UPDATE OF captain_id
  ON teams
  FOR EACH ROW
  EXECUTE FUNCTION sync_captain_role_from_captain_id();
