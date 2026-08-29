-- L5: Drop duplicate indexes and constraints (cascade migration artifacts)

-- match_map_vetos: orphan _fkey_new duplicate of _fkey (same column, same target)
ALTER TABLE match_map_vetos DROP CONSTRAINT IF EXISTS match_map_vetos_match_id_fkey_new;

-- tournament_invitations.code: standalone unique index duplicates the table-level UNIQUE constraint
DROP INDEX IF EXISTS ux_tournament_invitations_code;

-- teams.tag: plain btree duplicates the implicit index from the UNIQUE constraint
DROP INDEX IF EXISTS idx_teams_tag;

-- tournaments.season_id: full-scan index is subsumed by the partial (WHERE season_id IS NOT NULL)
DROP INDEX IF EXISTS idx_tournaments_season;
