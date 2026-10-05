-- Drop plain non-unique indexes that are fully covered by a unique index on the same columns.
-- A unique index already serves all lookup needs; the plain duplicate adds write overhead with no benefit.

-- booking_rules
DROP INDEX IF EXISTS public.idx_booking_rules_venue;

-- daily_stats
DROP INDEX IF EXISTS public.idx_daily_stats_venue_date;

-- feature_flags
DROP INDEX IF EXISTS public.idx_feature_flags_key;

-- loyalty_accounts
DROP INDEX IF EXISTS public.idx_loyalty_accounts_user;

-- organizations
DROP INDEX IF EXISTS public.idx_organizations_slug;
DROP INDEX IF EXISTS public.idx_organizations_owner;

-- partner_sponsor_invitations
DROP INDEX IF EXISTS public.partner_sponsor_invitations_token_hash_idx;

-- player_steam_accounts
DROP INDEX IF EXISTS public.idx_player_steam_accounts_steam64_id;

-- profiles
DROP INDEX IF EXISTS public.idx_profiles_slug;
DROP INDEX IF EXISTS public.idx_profiles_username;

-- riot_accounts
DROP INDEX IF EXISTS public.idx_riot_accounts_user_id;
DROP INDEX IF EXISTS public.idx_riot_accounts_puuid;

-- tournaments
DROP INDEX IF EXISTS public.idx_tournaments_slug;

-- venue_loyalty_config
DROP INDEX IF EXISTS public.idx_venue_loyalty_config_venue;

-- broadcast_licenses (may not exist on all envs)
DROP INDEX IF EXISTS public.idx_broadcast_licenses_license_key;

-- public_tool_brackets (partial plain covered by full unique)
DROP INDEX IF EXISTS public.idx_public_tool_brackets_share_token;

-- verified_roles (plain and partial-plain covered by unique equivalents)
DROP INDEX IF EXISTS public.idx_verified_roles_user_role;
DROP INDEX IF EXISTS public.idx_verified_roles_active;

-- tournament_participants (plain covered by unique)
DROP INDEX IF EXISTS public.idx_tournament_participants_tournament_team;

-- user_roles (plain covered by uq_user_roles_user_role unique)
DROP INDEX IF EXISTS public.idx_user_roles_user_role;

-- Identical plain duplicates on staff_audit_log (idx_audit_* are older aliases)
DROP INDEX IF EXISTS public.idx_audit_action;
DROP INDEX IF EXISTS public.idx_audit_created;

-- Duplicate unique indexes: drop auto-generated _key constraint, keep explicit uq_ index.
-- Must drop constraint first; the backing index is removed with it.
ALTER TABLE public.user_roles DROP CONSTRAINT IF EXISTS user_roles_user_role_key;
DROP INDEX IF EXISTS public.user_roles_user_role_key;

ALTER TABLE public.verified_roles DROP CONSTRAINT IF EXISTS verified_roles_user_role_key;
DROP INDEX IF EXISTS public.verified_roles_user_role_key;

ALTER TABLE public.team_roster_members DROP CONSTRAINT IF EXISTS team_roster_members_roster_id_user_id_key;
DROP INDEX IF EXISTS public.team_roster_members_roster_id_user_id_key;
