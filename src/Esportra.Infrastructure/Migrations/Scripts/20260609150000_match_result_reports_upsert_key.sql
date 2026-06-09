-- Supports ON CONFLICT (match_id, game_number, reported_by_team_id) upsert in MatchSystemEndpoints.
-- Non-partial index: inserts always supply reported_by_team_id; avoids partial-index inference mismatch.
CREATE UNIQUE INDEX IF NOT EXISTS idx_match_result_reports_match_game_reporter
    ON public.match_result_reports (match_id, game_number, reported_by_team_id);