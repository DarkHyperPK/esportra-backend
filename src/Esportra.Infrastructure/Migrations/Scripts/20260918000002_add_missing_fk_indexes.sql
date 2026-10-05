-- Add indexes on FK columns that were missing, causing sequential scans on join paths.
-- Focused on high-traffic tables identified via pg_stat_user_tables seq_scan analysis.

-- match_messages: chat loaded per match on every match page open
CREATE INDEX IF NOT EXISTS idx_match_messages_match_id ON public.match_messages(match_id);
CREATE INDEX IF NOT EXISTS idx_match_messages_sender_id ON public.match_messages(sender_id);

-- match_time_proposals: confirmed 99.9% sequential scan rate
CREATE INDEX IF NOT EXISTS idx_match_time_proposals_match_id ON public.match_time_proposals(match_id);

-- brkt_match_games: game results and MVP loaded per match
CREATE INDEX IF NOT EXISTS idx_brkt_match_games_map_id ON public.brkt_match_games(map_id);
CREATE INDEX IF NOT EXISTS idx_brkt_match_games_mvp_id ON public.brkt_match_games(mvp_id);

-- map veto flow
CREATE INDEX IF NOT EXISTS idx_match_map_veto_actions_map_id ON public.match_map_veto_actions(map_id);
CREATE INDEX IF NOT EXISTS idx_match_map_vetos_stage_id ON public.match_map_vetos(stage_id);
CREATE INDEX IF NOT EXISTS idx_match_map_vetos_tournament_id ON public.match_map_vetos(tournament_id);

-- BR lobby readiness
CREATE INDEX IF NOT EXISTS idx_br_lobby_readiness_user_id ON public.br_lobby_readiness(user_id);
CREATE INDEX IF NOT EXISTS idx_br_lobby_readiness_game_id ON public.br_lobby_readiness(game_id);

-- tournament disputes: dispute lookup by match
CREATE INDEX IF NOT EXISTS idx_tournament_disputes_match_id ON public.tournament_disputes(match_id);

-- game servers: server lookup per tournament
CREATE INDEX IF NOT EXISTS idx_game_servers_tournament_id ON public.game_servers(tournament_id);

-- team_invitations: was 100% sequential scan
CREATE INDEX IF NOT EXISTS idx_team_invitations_team_id ON public.team_invitations(team_id);
CREATE INDEX IF NOT EXISTS idx_team_invitations_invited_user_id ON public.team_invitations(invited_user_id);
CREATE INDEX IF NOT EXISTS idx_team_invitations_invited_by_user_id ON public.team_invitations(invited_by_user_id);
CREATE INDEX IF NOT EXISTS idx_team_invitations_roster_id ON public.team_invitations(roster_id);

-- match disputes
CREATE INDEX IF NOT EXISTS idx_match_disputes_disputed_by ON public.match_disputes(disputed_by_user_id);

-- brkt_match_events: event timeline loaded per match
CREATE INDEX IF NOT EXISTS idx_brkt_match_events_created_by ON public.brkt_match_events(created_by);

-- tournament bans
CREATE INDEX IF NOT EXISTS idx_tournament_bans_participant_id ON public.tournament_bans(participant_id);
CREATE INDEX IF NOT EXISTS idx_tournament_bans_banned_by ON public.tournament_bans(banned_by);

-- ghost sessions: approval lookup
CREATE INDEX IF NOT EXISTS idx_ghost_sessions_approval_id ON public.ghost_sessions(approval_id);

-- match result reports
CREATE INDEX IF NOT EXISTS idx_match_result_reports_map_id ON public.match_result_reports(map_id);

-- sponsor analytics: tournament filter
CREATE INDEX IF NOT EXISTS idx_sponsor_analytics_events_tournament_id ON public.sponsor_analytics_events(tournament_id);

-- organization media
CREATE INDEX IF NOT EXISTS idx_organization_media_organization_id ON public.organization_media(organization_id);
CREATE INDEX IF NOT EXISTS idx_organization_media_album_id ON public.organization_media(album_id);
CREATE INDEX IF NOT EXISTS idx_organization_albums_organization_id ON public.organization_albums(organization_id);

-- licenses: user lookup
CREATE INDEX IF NOT EXISTS idx_licenses_user_id ON public.licenses(user_id);

-- match checkins
CREATE INDEX IF NOT EXISTS idx_match_checkins_user_id ON public.match_checkins(user_id);

-- tournament invitations: redemption lookup
CREATE INDEX IF NOT EXISTS idx_tournament_invitations_redeemed_by ON public.tournament_invitations(redeemed_by);
CREATE INDEX IF NOT EXISTS idx_tournament_invitations_redeemed_team_id ON public.tournament_invitations(redeemed_team_id);
