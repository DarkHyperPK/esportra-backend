-- H5: tournaments.organizer_id FK (dangling UUID when profile hard-deleted)
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'tournaments_organizer_id_fkey') THEN
    ALTER TABLE tournaments
        ADD CONSTRAINT tournaments_organizer_id_fkey
        FOREIGN KEY (organizer_id) REFERENCES profiles(id) ON DELETE RESTRICT;
  END IF;
END $$;

-- H5: teams.owner_id FK
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'teams_owner_id_fkey') THEN
    ALTER TABLE teams
        ADD CONSTRAINT teams_owner_id_fkey
        FOREIGN KEY (owner_id) REFERENCES profiles(id) ON DELETE RESTRICT;
  END IF;
END $$;

-- H10: Require soft-delete before hard-delete on tournaments
-- Prevents a one-shot DELETE FROM tournaments from cascading through 15+ tables.
CREATE OR REPLACE FUNCTION prevent_tournament_hard_delete()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.deleted_at IS NULL THEN
        RAISE EXCEPTION
            'Tournament % must be soft-deleted (deleted_at set) before it can be permanently deleted.',
            OLD.id
            USING ERRCODE = 'P0001';
    END IF;
    RETURN OLD;
END;
$$;

DROP TRIGGER IF EXISTS trg_prevent_tournament_hard_delete ON tournaments;
CREATE TRIGGER trg_prevent_tournament_hard_delete
    BEFORE DELETE ON tournaments
    FOR EACH ROW EXECUTE FUNCTION prevent_tournament_hard_delete();
