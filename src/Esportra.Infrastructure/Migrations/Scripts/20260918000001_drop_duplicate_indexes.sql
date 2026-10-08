-- Drop plain non-unique indexes that are fully covered by a unique index on the same columns.
-- Preceded by DROP CONSTRAINT guards per migration rules (no-op when index is not constraint-backed).

-- booking_rules
ALTER TABLE public.booking_rules DROP CONSTRAINT IF EXISTS idx_booking_rules_venue;
DROP INDEX IF EXISTS idx_booking_rules_venue;

-- daily_stats
ALTER TABLE public.daily_stats DROP CONSTRAINT IF EXISTS idx_daily_stats_venue_date;
DROP INDEX IF EXISTS idx_daily_stats_venue_date;

-- feature_flags
ALTER TABLE public.feature_flags DROP CONSTRAINT IF EXISTS idx_feature_flags_key;
DROP INDEX IF EXISTS idx_feature_flags_key;

-- loyalty_accounts
ALTER TABLE public.loyalty_accounts DROP CONSTRAINT IF EXISTS idx_loyalty_accounts_user;
DROP INDEX IF EXISTS idx_loyalty_accounts_user;

-- organizations
ALTER TABLE public.organizations DROP CONSTRAINT IF EXISTS idx_organizations_slug;
DROP INDEX IF EXISTS idx_organizations_slug;
ALTER TABLE public.organizations DROP CONSTRAINT IF EXISTS idx_organizations_owner;
DROP INDEX IF EXISTS idx_organizations_owner;

-- partner_sponsor_invitations
ALTER TABLE public.partner_sponsor_invitations DROP CONSTRAINT IF EXISTS partner_sponsor_invitations_token_hash_idx;
DROP INDEX IF EXISTS partner_sponsor_invitations_token_hash_idx;

-- player_steam_accounts
ALTER TABLE public.player_steam_accounts DROP CONSTRAINT IF EXISTS idx_player_steam_accounts_steam64_id;
DROP INDEX IF EXISTS idx_player_steam_accounts_steam64_id;

-- profiles
ALTER TABLE public.profiles DROP CONSTRAINT IF EXISTS idx_profiles_slug;
DROP INDEX IF EXISTS idx_profiles_slug;
ALTER TABLE public.profiles DROP CONSTRAINT IF EXISTS idx_profiles_username;
DROP INDEX IF EXISTS idx_profiles_username;

-- riot_accounts
ALTER TABLE public.riot_accounts DROP CONSTRAINT IF EXISTS idx_riot_accounts_user_id;
DROP INDEX IF EXISTS idx_riot_accounts_user_id;
ALTER TABLE public.riot_accounts DROP CONSTRAINT IF EXISTS idx_riot_accounts_puuid;
DROP INDEX IF EXISTS idx_riot_accounts_puuid;

-- tournaments
ALTER TABLE public.tournaments DROP CONSTRAINT IF EXISTS idx_tournaments_slug;
DROP INDEX IF EXISTS idx_tournaments_slug;

-- venue_loyalty_config
ALTER TABLE public.venue_loyalty_config DROP CONSTRAINT IF EXISTS idx_venue_loyalty_config_venue;
DROP INDEX IF EXISTS idx_venue_loyalty_config_venue;

-- broadcast_licenses (may not exist on all envs)
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tablename = 'broadcast_licenses') THEN
    ALTER TABLE public.broadcast_licenses DROP CONSTRAINT IF EXISTS idx_broadcast_licenses_license_key;
    DROP INDEX IF EXISTS idx_broadcast_licenses_license_key;
  END IF;
END $$;

-- public_tool_brackets (partial plain covered by full unique)
ALTER TABLE public.public_tool_brackets DROP CONSTRAINT IF EXISTS idx_public_tool_brackets_share_token;
DROP INDEX IF EXISTS idx_public_tool_brackets_share_token;

-- verified_roles (plain and partial-plain covered by unique equivalents)
ALTER TABLE public.verified_roles DROP CONSTRAINT IF EXISTS idx_verified_roles_user_role;
DROP INDEX IF EXISTS idx_verified_roles_user_role;
ALTER TABLE public.verified_roles DROP CONSTRAINT IF EXISTS idx_verified_roles_active;
DROP INDEX IF EXISTS idx_verified_roles_active;

-- tournament_participants (plain covered by unique)
ALTER TABLE public.tournament_participants DROP CONSTRAINT IF EXISTS idx_tournament_participants_tournament_team;
DROP INDEX IF EXISTS idx_tournament_participants_tournament_team;

-- user_roles (plain covered by uq_user_roles_user_role unique)
ALTER TABLE public.user_roles DROP CONSTRAINT IF EXISTS idx_user_roles_user_role;
DROP INDEX IF EXISTS idx_user_roles_user_role;

-- Identical plain duplicates on staff_audit_log (idx_audit_* are older aliases)
ALTER TABLE public.staff_audit_log DROP CONSTRAINT IF EXISTS idx_audit_action;
DROP INDEX IF EXISTS idx_audit_action;
ALTER TABLE public.staff_audit_log DROP CONSTRAINT IF EXISTS idx_audit_created;
DROP INDEX IF EXISTS idx_audit_created;

-- Duplicate unique indexes: drop auto-generated _key constraint, keep explicit uq_ index.
-- Constraint must be dropped first; the backing index is removed with it.
ALTER TABLE public.user_roles DROP CONSTRAINT IF EXISTS user_roles_user_role_key;
DROP INDEX IF EXISTS user_roles_user_role_key;

ALTER TABLE public.verified_roles DROP CONSTRAINT IF EXISTS verified_roles_user_role_key;
DROP INDEX IF EXISTS verified_roles_user_role_key;

ALTER TABLE public.team_roster_members DROP CONSTRAINT IF EXISTS team_roster_members_roster_id_user_id_key;
DROP INDEX IF EXISTS team_roster_members_roster_id_user_id_key;
