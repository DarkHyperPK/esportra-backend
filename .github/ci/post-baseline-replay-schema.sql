-- CI post-baseline replay schema — pg_dump from staging.
-- DO NOT EDIT BY HAND. Regenerate: bash .github/scripts/dump-replay-schema.sh

-- ── Supabase platform prerequisites ──────────────────────────────────────────
-- These exist in every Supabase instance but not in vanilla Postgres.

-- Schemas
CREATE SCHEMA IF NOT EXISTS auth;
CREATE SCHEMA IF NOT EXISTS storage;
CREATE SCHEMA IF NOT EXISTS extensions;

-- Extensions (only those referenced by public schema objects)
CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA extensions;
CREATE EXTENSION IF NOT EXISTS "uuid-ossp" WITH SCHEMA extensions;

-- Roles (referenced by RLS policies in the dump)
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
    CREATE ROLE anon NOLOGIN;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
    CREATE ROLE authenticated NOLOGIN;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'service_role') THEN
    CREATE ROLE service_role NOLOGIN BYPASSRLS;
  END IF;
END $$;

-- auth.users (FK target for public.profiles, user_roles, etc.)
CREATE TABLE IF NOT EXISTS auth.users (
    id uuid NOT NULL PRIMARY KEY,
    email character varying(255),
    created_at timestamp with time zone,
    deleted_at timestamp with time zone
);

-- auth helper functions (referenced by RLS policies)
CREATE OR REPLACE FUNCTION auth.uid() RETURNS uuid
    LANGUAGE sql STABLE
    AS $$ SELECT NULLIF(current_setting('request.jwt.claim.sub', true), '')::uuid; $$;

CREATE OR REPLACE FUNCTION auth.role() RETURNS text
    LANGUAGE sql STABLE
    AS $$ SELECT COALESCE(current_setting('request.jwt.claim.role', true), 'anon'); $$;

CREATE OR REPLACE FUNCTION auth.jwt() RETURNS jsonb
    LANGUAGE sql STABLE
    AS $$ SELECT '{}'::jsonb; $$;

-- storage.objects (referenced by functions via %ROWTYPE)
CREATE TABLE IF NOT EXISTS storage.objects (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    bucket_id text,
    name text,
    owner uuid,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb
);

-- Disable function body validation (functions may reference tables
-- that appear later in the dump due to pg_dump ordering limitations).
SET check_function_bodies = off;

-- ── Public schema from staging pg_dump ───────────────────────────────────────







CREATE TYPE public.app_role AS ENUM (
    'casual',
    'organizer',
    'venue_owner',
    'admin'
);



CREATE TYPE public.invite_status AS ENUM (
    'pending',
    'accepted',
    'declined',
    'expired'
);



CREATE TYPE public.ledger_type AS ENUM (
    'credit',
    'debit'
);



CREATE TYPE public.match_status AS ENUM (
    'scheduled',
    'in_progress',
    'completed',
    'cancelled'
);



CREATE TYPE public.notification_type AS ENUM (
    'info',
    'success',
    'warning',
    'error',
    'team_invite',
    'staff_invite',
    'tournament_announcement',
    'result_reported',
    'result_accepted',
    'result_disputed',
    'dispute_filed',
    'dispute_resolved',
    'dispute_rejected',
    'veto_your_turn',
    'veto_completed',
    'match_ready',
    'match_completed',
    'tournament_registered',
    'team_invite_response',
    'tournament_invite',
    'match_schedule_changed',
    'match_walkover',
    'broadcast'
);



CREATE TYPE public.payment_status AS ENUM (
    'pending',
    'succeeded',
    'failed',
    'refunded'
);



CREATE TYPE public.payout_status AS ENUM (
    'requested',
    'approved',
    'rejected',
    'paid',
    'failed'
);



CREATE TYPE public.qualification_status AS ENUM (
    'qualified',
    'eliminated',
    'pending'
);



CREATE TYPE public.registration_status AS ENUM (
    'pending',
    'approved',
    'rejected',
    'cancelled',
    'checked_in',
    'eliminated',
    'disqualified',
    'waitlist'
);



CREATE TYPE public.registration_type AS ENUM (
    'solo',
    'team'
);



CREATE TYPE public.roster_member_role AS ENUM (
    'starter',
    'substitute',
    'coach'
);



CREATE TYPE public.season_status AS ENUM (
    'draft',
    'published',
    'live',
    'completed',
    'archived'
);



CREATE TYPE public.seed_mode AS ENUM (
    'random',
    'manual',
    'top_seeded'
);



CREATE TYPE public.team_member_role AS ENUM (
    'owner',
    'captain',
    'member',
    'substitute',
    'coach'
);



CREATE TYPE public.tournament_format AS ENUM (
    'single_elimination',
    'double_elimination',
    'round_robin',
    'swiss',
    'custom',
    'battle_royale'
);



CREATE TYPE public.tournament_role AS ENUM (
    'qualifier',
    'event',
    'finals',
    'custom'
);



CREATE TYPE public.tournament_status AS ENUM (
    'draft',
    'open',
    'closed',
    'check_in',
    'ongoing',
    'completed',
    'cancelled',
    'published'
);



CREATE TYPE public.verification_status AS ENUM (
    'unverified',
    'pending',
    'verified'
);



CREATE TYPE public.wallet_owner AS ENUM (
    'platform',
    'organizer',
    'team',
    'user'
);



CREATE FUNCTION public.accept_team_invite(invite_id uuid) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
declare
  v_team uuid;
  v_roster uuid;
  v_invited uuid;
  v_status text;
  v_size int;
  v_count int;
begin
  select team_id, roster_id, invited_user_id, status into v_team, v_roster, v_invited, v_status
  from public.team_invitations where id = invite_id for update;

  if not found then
    raise exception 'Invite not found';
  end if;
  if v_invited <> auth.uid() then
    raise exception 'Not your invite';
  end if;
  if v_status <> 'pending' then
    return;
  end if;

  -- Ensure team member
  insert into public.team_members (team_id, user_id, role, is_active)
  values (v_team, v_invited, 'member', true)
  on conflict (team_id, user_id) do nothing;

  -- Add to roster if specified, respecting 5v5 cap (7) else team_size
  if v_roster is not null then
    select team_size into v_size from public.team_rosters where id = v_roster;
    if v_size is null then v_size := 5; end if;
    select count(*) into v_count from public.team_roster_members where roster_id = v_roster;
    if (v_size = 5 and v_count < 7) or (v_size <> 5 and v_count < v_size) then
      insert into public.team_roster_members (roster_id, user_id) values (v_roster, v_invited)
      on conflict do nothing;
    end if;
  end if;

  update public.team_invitations set status = 'accepted', responded_at = now() where id = invite_id;
end;
$$;



CREATE FUNCTION public.admin_clear_tournament_winner(p_tournament_id uuid, p_reopen_completed boolean DEFAULT false) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
    UPDATE public.tournaments
       SET winner_id = NULL,
           status = CASE
               WHEN p_reopen_completed AND status::text = 'completed'
                   THEN 'open'::public.tournament_status
               ELSE status
           END,
           updated_at = NOW()
     WHERE id = p_tournament_id
       AND winner_id IS NOT NULL;
END;
$$;



CREATE FUNCTION public.admin_set_tournament_winner(p_tournament_id uuid, p_winner_id uuid) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
  UPDATE tournaments
  SET winner_id = p_winner_id,
      status = 'completed'::tournament_status,
      end_date = NOW()
  WHERE id = p_tournament_id;
END;
$$;



CREATE FUNCTION public.admin_suspend_user(target_user_id uuid, reason text, duration text, type text, until_time timestamp with time zone) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    AS $$
BEGIN
  -- Verify caller is an admin
  IF NOT EXISTS (
    SELECT 1 FROM profiles 
    WHERE id = auth.uid() AND (is_admin = true OR role = 'admin')
  ) THEN
    RAISE EXCEPTION 'Not authorized to suspend users';
  END IF;

  -- Update target user
  UPDATE profiles
  SET 
    is_suspended = true,
    suspension_reason = reason,
    suspension_type = type,
    suspension_until = until_time,
    updated_at = NOW()
  WHERE id = target_user_id;
END;
$$;



CREATE FUNCTION public.admin_unsuspend_user(target_user_id uuid) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    AS $$
BEGIN
  -- Verify caller is an admin
  IF NOT EXISTS (
    SELECT 1 FROM profiles 
    WHERE id = auth.uid() AND (is_admin = true OR role = 'admin')
  ) THEN
    RAISE EXCEPTION 'Not authorized to unsuspend users';
  END IF;

  -- Update target user
  UPDATE profiles
  SET 
    is_suspended = false,
    suspension_reason = null,
    suspension_type = null,
    suspension_until = null,
    updated_at = NOW()
  WHERE id = target_user_id;
END;
$$;



CREATE FUNCTION public.advance_match_v2(p_match_id uuid, p_winner_id uuid) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
    v_match RECORD;
    v_loser_id UUID;
    v_next_match_id UUID;
    v_loser_next_match_id UUID;
BEGIN
    -- Get current match details
    SELECT * INTO v_match
    FROM public.tournament_matches
    WHERE id = p_match_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Match not found';
    END IF;

    -- Determine the loser
    IF v_match.team1_id = p_winner_id THEN
        v_loser_id := v_match.team2_id;
    ELSIF v_match.team2_id = p_winner_id THEN
        v_loser_id := v_match.team1_id;
    ELSE
        RAISE EXCEPTION 'Winner ID does not match either team in the match';
    END IF;

    -- Update current match with winner
    UPDATE public.tournament_matches
    SET winner_id = p_winner_id,
        status = 'completed',
        updated_at = NOW()
    WHERE id = p_match_id;

    -- Get advancement targets
    v_next_match_id := v_match.next_match_id;
    v_loser_next_match_id := v_match.loser_next_match_id;

    -- Advance winner to next match (if exists)
    IF v_next_match_id IS NOT NULL THEN
        UPDATE public.tournament_matches
        SET team1_id = CASE 
            WHEN team1_id IS NULL THEN p_winner_id
            WHEN team2_id IS NULL THEN team1_id
            ELSE team1_id
        END,
        team2_id = CASE
            WHEN team1_id IS NULL THEN team2_id
            WHEN team2_id IS NULL THEN p_winner_id
            ELSE team2_id
        END,
        updated_at = NOW()
        WHERE id = v_next_match_id;
    END IF;

    -- Advance loser to losers bracket (if exists)
    IF v_loser_next_match_id IS NOT NULL AND v_loser_id IS NOT NULL THEN
        UPDATE public.tournament_matches
        SET team1_id = CASE 
            WHEN team1_id IS NULL THEN v_loser_id
            WHEN team2_id IS NULL THEN team1_id
            ELSE team1_id
        END,
        team2_id = CASE
            WHEN team1_id IS NULL THEN team2_id
            WHEN team2_id IS NULL THEN v_loser_id
            ELSE team2_id
        END,
        updated_at = NOW()
        WHERE id = v_loser_next_match_id;
    END IF;

END;
$$;



CREATE FUNCTION public.advance_match_v2(p_match_id uuid, p_winner_id uuid, p_team1_score integer, p_team2_score integer) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
    v_match RECORD;
    v_loser_id UUID;
    v_next_match RECORD;
    v_loser_next_match RECORD;
BEGIN
    -- 1. Get current match details
    SELECT * INTO v_match FROM tournament_matches WHERE id = p_match_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Match not found';
    END IF;

    -- 2. Determine loser
    IF p_winner_id = v_match.team1_id THEN
        v_loser_id := v_match.team2_id;
    ELSIF p_winner_id = v_match.team2_id THEN
        v_loser_id := v_match.team1_id;
    ELSE
        RAISE EXCEPTION 'Winner ID must be one of the participants';
    END IF;

    -- 3. Update current match
    UPDATE tournament_matches
    SET 
        winner_id = p_winner_id,
        team1_score = p_team1_score,
        team2_score = p_team2_score,
        status = 'completed',
        updated_at = NOW()
    WHERE id = p_match_id;

    -- 4. Advance Winner
    IF v_match.next_match_id IS NOT NULL THEN
        SELECT * INTO v_next_match FROM tournament_matches WHERE id = v_match.next_match_id;
        
        -- Special handling for Grand Final Reset
        IF v_match.bracket_side = 'final' THEN
            -- Only advance to reset if the Losers Bracket team (team2) wins
            IF p_winner_id = v_match.team2_id THEN
                UPDATE tournament_matches 
                SET team1_id = v_match.team1_id,
                    team2_id = v_match.team2_id,
                    status = 'pending',
                    updated_at = NOW()
                WHERE id = v_match.next_match_id;
            ELSE
                -- WB team won, cancel the reset match
                UPDATE tournament_matches 
                SET status = 'cancelled',
                    updated_at = NOW()
                WHERE id = v_match.next_match_id;
            END IF;
        ELSE
            -- Standard advancement
            IF v_next_match.team1_id IS NULL THEN
                UPDATE tournament_matches SET team1_id = p_winner_id WHERE id = v_match.next_match_id;
            ELSIF v_next_match.team2_id IS NULL THEN
                UPDATE tournament_matches SET team2_id = p_winner_id WHERE id = v_match.next_match_id;
            ELSE
                -- If both slots are full, update the one that matches the winner if it was already there (re-reporting)
                UPDATE tournament_matches 
                SET team1_id = CASE WHEN team1_id = p_winner_id THEN p_winner_id ELSE team1_id END,
                    team2_id = CASE WHEN team2_id = p_winner_id THEN p_winner_id ELSE team2_id END
                WHERE id = v_match.next_match_id;
            END IF;
        END IF;
    END IF;

    -- 5. Advance Loser (Double Elimination)
    IF v_match.loser_next_match_id IS NOT NULL AND v_loser_id IS NOT NULL THEN
        SELECT * INTO v_loser_next_match FROM tournament_matches WHERE id = v_match.loser_next_match_id;
        
        IF v_loser_next_match.team1_id IS NULL THEN
            UPDATE tournament_matches SET team1_id = v_loser_id WHERE id = v_match.loser_next_match_id;
        ELSIF v_loser_next_match.team2_id IS NULL THEN
            UPDATE tournament_matches SET team2_id = v_loser_id WHERE id = v_match.loser_next_match_id;
        END IF;
    END IF;

END;
$$;



CREATE FUNCTION public.advance_teams_to_next_stage(p_current_stage_id uuid) RETURNS integer
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
    v_tournament_id UUID;
    v_next_stage_id UUID;
    v_advancement_count INTEGER;
    v_advanced_count INTEGER := 0;
    v_team_id UUID;
BEGIN
    -- 1. Get current stage details
    SELECT tournament_id, advancement_count INTO v_tournament_id, v_advancement_count
    FROM tournament_stages
    WHERE id = p_current_stage_id;

    IF v_advancement_count IS NULL OR v_advancement_count <= 0 THEN
        RAISE EXCEPTION 'Advancement count not set for this stage';
    END IF;

    -- 2. Find next stage
    SELECT id INTO v_next_stage_id
    FROM tournament_stages
    WHERE tournament_id = v_tournament_id
    AND stage_order > (SELECT stage_order FROM tournament_stages WHERE id = p_current_stage_id)
    ORDER BY stage_order ASC
    LIMIT 1;

    IF v_next_stage_id IS NULL THEN
        RAISE EXCEPTION 'No next stage found';
    END IF;

    -- 3. Identify top N teams based on wins and round reached
    FOR v_team_id IN (
        WITH team_performance AS (
            SELECT 
                team_id,
                MAX(round) as max_round,
                COUNT(*) FILTER (WHERE winner_id = team_id) as wins,
                COUNT(*) FILTER (WHERE winner_id IS NOT NULL AND winner_id != team_id) as losses,
                SUM(CASE WHEN winner_id = team_id THEN 3 ELSE 0 END) as points -- Standard 3 points for win
            FROM (
                SELECT team1_id as team_id, round, winner_id FROM tournament_matches WHERE stage_id = p_current_stage_id AND team1_id IS NOT NULL
                UNION ALL
                SELECT team2_id as team_id, round, winner_id FROM tournament_matches WHERE stage_id = p_current_stage_id AND team2_id IS NOT NULL
            ) all_teams
            GROUP BY team_id
        )
        SELECT tp.team_id
        FROM team_performance tp
        ORDER BY tp.points DESC, tp.max_round DESC, tp.wins DESC
        LIMIT v_advancement_count
    ) LOOP
        -- 4. Enroll in next stage
        PERFORM public.enroll_team_in_stage(v_next_stage_id, v_team_id);
        v_advanced_count := v_advanced_count + 1;
    END LOOP;

    RETURN v_advanced_count;
END;
$$;



CREATE FUNCTION public.approve_verification_request(request_id uuid, admin_notes text DEFAULT NULL::text) RETURNS json
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
    request_record RECORD;
    result JSON;
BEGIN
    -- Get the verification request
    SELECT * INTO request_record 
    FROM public.verification_requests 
    WHERE id = request_id AND status = 'pending';
    
    IF NOT FOUND THEN
        RETURN json_build_object('success', false, 'message', 'Verification request not found or not pending');
    END IF;
    
    -- Update the request status
    UPDATE public.verification_requests 
    SET 
        status = 'approved',
        reviewed_by = auth.uid(),
        reviewed_at = NOW(),
        verification_notes = admin_notes
    WHERE id = request_id;
    
    -- Add to verified_roles
    INSERT INTO public.verified_roles (user_id, role, verified_by, verification_request_id)
    VALUES (request_record.user_id, request_record.requested_role, auth.uid(), request_id);
    
    -- Update user profile
    UPDATE public.profiles 
    SET 
        role = request_record.requested_role,
        is_verified = true,
        verification_status = 'verified'
    WHERE id = request_record.user_id;
    
    result := json_build_object(
        'success', true,
        'message', 'Verification request approved successfully'
    );
    
    RETURN result;
END;
$$;



CREATE FUNCTION public.assign_teams_to_bracket(p_stage_id uuid) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
    v_team_ids UUID[];
    v_match RECORD;
    v_team_idx INTEGER := 1;
    v_team_count INTEGER;
BEGIN
    -- Get all teams from stage_participants, ordered for seeding
    SELECT ARRAY_AGG(team_id ORDER BY seed NULLS LAST, team_id) INTO v_team_ids
    FROM stage_participants
    WHERE stage_id = p_stage_id AND team_id IS NOT NULL;
    
    IF v_team_ids IS NULL THEN
        RAISE NOTICE 'No teams in stage_participants for stage %', p_stage_id;
        RETURN;
    END IF;
    
    v_team_count := array_length(v_team_ids, 1);
    RAISE NOTICE 'Assigning % teams to bracket', v_team_count;
    
    -- Get all Round 1 Winners bracket matches ordered by match_number
    FOR v_match IN (
        SELECT id, match_number
        FROM tournament_matches
        WHERE stage_id = p_stage_id 
          AND round = 1 
          AND (bracket_side = 'winners' OR bracket_side IS NULL)
        ORDER BY match_number
    ) LOOP
        -- Assign team1 (top seed)
        IF v_team_idx <= v_team_count THEN
            UPDATE tournament_matches 
            SET team1_id = v_team_ids[v_team_idx]
            WHERE id = v_match.id;
            v_team_idx := v_team_idx + 1;
        END IF;
        
        -- Assign team2 (bottom seed - pair from opposite end)
        IF v_team_idx <= v_team_count THEN
            UPDATE tournament_matches 
            SET team2_id = v_team_ids[v_team_idx]
            WHERE id = v_match.id;
            v_team_idx := v_team_idx + 1;
        END IF;
    END LOOP;
    
    RAISE NOTICE 'Assigned % teams to Round 1 matches', v_team_idx - 1;
END;
$$;



CREATE FUNCTION public.assign_user_role(p_user_id uuid, p_role text, p_assigned_by uuid DEFAULT NULL::uuid, p_is_primary boolean DEFAULT false) RETURNS boolean
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
  role_exists BOOLEAN;
BEGIN
  -- Check if user already has this role
  SELECT EXISTS(
    SELECT 1 FROM public.user_roles 
    WHERE user_id = p_user_id AND role = p_role
  ) INTO role_exists;
  
  IF role_exists THEN
    -- Update existing role to active
    UPDATE public.user_roles 
    SET is_active = true, 
        assigned_by = p_assigned_by,
        assigned_at = NOW(),
        updated_at = NOW()
    WHERE user_id = p_user_id AND role = p_role;
  ELSE
    -- Insert new role
    INSERT INTO public.user_roles (user_id, role, assigned_by, is_primary)
    VALUES (p_user_id, p_role, p_assigned_by, p_is_primary);
  END IF;
  
  -- If this is set as primary, unset other primary roles
  IF p_is_primary THEN
    UPDATE public.user_roles 
    SET is_primary = false, updated_at = NOW()
    WHERE user_id = p_user_id AND role != p_role;
  END IF;
  
  RETURN true;
END;
$$;



CREATE FUNCTION public.auto_earn_loyalty_on_session_end() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
    v_config RECORD; v_duration_hrs NUMERIC; v_points INTEGER;
    v_account_id UUID; v_lifetime BIGINT; v_new_tier TEXT := NULL;
    v_tier RECORD;
BEGIN
    IF NEW.user_id IS NULL THEN RETURN NEW; END IF;
    SELECT vlc.* INTO v_config FROM venue_loyalty_config vlc WHERE vlc.venue_id = NEW.venue_id AND vlc.is_active = true LIMIT 1;
    IF NOT FOUND THEN RETURN NEW; END IF;
    v_duration_hrs := EXTRACT(EPOCH FROM (NEW.ended_at - NEW.started_at)) / 3600.0;
    IF v_duration_hrs * 60 < COALESCE(v_config.min_session_minutes, 0) THEN RETURN NEW; END IF;
    v_points := GREATEST(1, CEIL(v_duration_hrs) * v_config.points_per_hour * COALESCE(v_config.bonus_multiplier, 1));
    INSERT INTO loyalty_accounts (user_id, total_points, lifetime_points, current_tier, venue_id)
    VALUES (NEW.user_id, v_points, v_points, 'bronze', NEW.venue_id)
    ON CONFLICT (user_id) DO UPDATE SET
        total_points = loyalty_accounts.total_points + v_points,
        lifetime_points = loyalty_accounts.lifetime_points + v_points,
        updated_at = NOW()
    RETURNING id, lifetime_points INTO v_account_id, v_lifetime;
    INSERT INTO loyalty_transactions (account_id, venue_id, type, points, description, session_id)
    VALUES (v_account_id, NEW.venue_id, 'earn', v_points,
            'Auto-earn: ' || ROUND(v_duration_hrs, 1) || 'h session', NEW.id);
    IF v_config.tiers IS NOT NULL AND jsonb_typeof(v_config.tiers) = 'array' THEN
        FOR v_tier IN SELECT * FROM jsonb_array_elements(v_config.tiers) AS t ORDER BY (t->>'min_points')::int DESC
        LOOP
            IF v_lifetime >= (v_tier.value->>'min_points')::int THEN
                v_new_tier := v_tier.value->>'name'; EXIT;
            END IF;
        END LOOP;
        IF v_new_tier IS NOT NULL THEN
            UPDATE loyalty_accounts SET current_tier = v_new_tier WHERE id = v_account_id AND current_tier IS DISTINCT FROM v_new_tier;
        END IF;
    END IF;
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.auto_transition_tournaments() RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$ BEGIN UPDATE tournaments SET status = 'ongoing' WHERE status IN ('open','published','check_in') AND start_date <= NOW(); UPDATE tournaments SET status = 'completed' WHERE status = 'ongoing' AND end_date IS NOT NULL AND end_date <= NOW(); END; $$;



CREATE FUNCTION public.auto_transition_tournaments_to_ongoing() RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
  UPDATE tournaments
  SET status = 'ongoing'::tournament_status, updated_at = NOW()
  WHERE status IN ('open'::tournament_status, 'published'::tournament_status, 'check_in'::tournament_status)
    AND start_date <= NOW()
    AND deleted_at IS NULL;
END;
$$;



CREATE FUNCTION public.block_organizer_sensitive_tournament_updates() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    AS $$
BEGIN
  -- Allow DB owner (covers SECURITY DEFINER functions like admin_set_tournament_winner)
  IF current_user = 'postgres' THEN
    RETURN NEW;
  END IF;

  -- Allow Supabase service_role connections (internal/admin API calls)
  IF current_setting('request.jwt.claim.role', true) = 'service_role' THEN
    RETURN NEW;
  END IF;

  -- Allow platform admins
  IF EXISTS (SELECT 1 FROM profiles WHERE id = auth.uid() AND is_admin = true) THEN
    RETURN NEW;
  END IF;

  -- Block organizers from updating sensitive columns
  IF NEW.is_featured IS DISTINCT FROM OLD.is_featured THEN
    RAISE EXCEPTION 'Only admins can change is_featured';
  END IF;

  IF NEW.status IS DISTINCT FROM OLD.status AND OLD.status::text = 'pending_approval' THEN
    RAISE EXCEPTION 'Only admins can approve tournaments';
  END IF;

  IF NEW.approved_by IS DISTINCT FROM OLD.approved_by THEN
    RAISE EXCEPTION 'Only admins can set approved_by';
  END IF;

  IF NEW.approved_at IS DISTINCT FROM OLD.approved_at THEN
    RAISE EXCEPTION 'Only admins can set approved_at';
  END IF;

  IF NEW.winner_id IS DISTINCT FROM OLD.winner_id THEN
    RAISE EXCEPTION 'Only admins can set winner_id';
  END IF;

  RETURN NEW;
END;
$$;



CREATE FUNCTION public.block_team_stats_update() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    AS $$
BEGIN
  -- Allow service_role and admins to update anything
  IF current_setting('request.jwt.claim.role', true) = 'service_role' THEN
    RETURN NEW;
  END IF;

  IF EXISTS (SELECT 1 FROM profiles WHERE id = auth.uid() AND is_admin = true) THEN
    RETURN NEW;
  END IF;

  -- Block everyone else from updating the stats column
  IF NEW.stats IS DISTINCT FROM OLD.stats THEN
    RAISE EXCEPTION 'Only the system can update team stats';
  END IF;

  RETURN NEW;
END;
$$;



CREATE FUNCTION public.block_venue_booking_payment_spoofing() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    AS $$
BEGIN
  IF current_setting('request.jwt.claim.role', true) = 'service_role' THEN
    RETURN NEW;
  END IF;
  IF EXISTS (SELECT 1 FROM profiles WHERE id = auth.uid() AND is_admin = true) THEN
    RETURN NEW;
  END IF;

  IF NEW.status IS DISTINCT FROM OLD.status THEN
    RAISE EXCEPTION 'Only the system can update booking status';
  END IF;
  IF NEW.payment_id IS DISTINCT FROM OLD.payment_id THEN
    RAISE EXCEPTION 'Only the system can update payment_id';
  END IF;
  IF NEW.total_amount IS DISTINCT FROM OLD.total_amount THEN
    RAISE EXCEPTION 'Only the system can update total_amount';
  END IF;

  RETURN NEW;
END;
$$;



CREATE FUNCTION public.cancel_booking(p_booking_id uuid, p_user_id uuid, p_reason text DEFAULT NULL::text) RETURNS jsonb
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
  v_booking venue_bookings%ROWTYPE;
BEGIN
  SELECT * INTO v_booking
  FROM venue_bookings
  WHERE id = p_booking_id
  FOR UPDATE;

  IF NOT FOUND THEN
    RETURN jsonb_build_object('success', false, 'message', 'Booking not found');
  END IF;

  IF v_booking.code_used_at IS NOT NULL THEN
    RETURN jsonb_build_object('success', false, 'message', 'Booking already used');
  END IF;

  IF v_booking.status = 'cancelled' THEN
    RETURN jsonb_build_object('success', false, 'message', 'Booking already cancelled');
  END IF;

  UPDATE venue_bookings
  SET status = 'cancelled',
      cancelled_at = NOW(),
      cancelled_by = p_user_id,
      cancellation_reason = p_reason,
      booking_code = NULL
  WHERE id = p_booking_id;

  RETURN jsonb_build_object('success', true, 'message', 'Booking cancelled');
END;
$$;



CREATE FUNCTION public.cascade_stage_bestof_to_vetos() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
BEGIN
    IF NEW.best_of IS DISTINCT FROM OLD.best_of THEN
        UPDATE public.match_map_vetos 
        SET best_of = NEW.best_of
        WHERE stage_id = NEW.id;
    END IF;
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.cleanup_expired_revoked_sessions() RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    AS $$
BEGIN
    DELETE FROM public.revoked_sessions WHERE expires_at < NOW() - INTERVAL '1 day';
END;
$$;



CREATE FUNCTION public.cleanup_old_soft_deletes() RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
  DELETE FROM public.tournaments
  WHERE deleted_at IS NOT NULL 
    AND deleted_at < NOW() - INTERVAL '7 days';

  DELETE FROM public.teams
  WHERE deleted_at IS NOT NULL 
    AND deleted_at < NOW() - INTERVAL '7 days';

  DELETE FROM public.venues
  WHERE deleted_at IS NOT NULL 
    AND deleted_at < NOW() - INTERVAL '7 days';
    
  RAISE NOTICE 'Cleaned up soft-deleted records older than 7 days';
END;
$$;



CREATE FUNCTION public.copy_storage_object(src_bucket text, src_name text, dest_name text) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    src_obj storage.objects%ROWTYPE;
BEGIN
    SELECT * INTO src_obj
    FROM storage.objects
    WHERE bucket_id = src_bucket AND name = src_name;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Object % not found in bucket %', src_name, src_bucket;
    END IF;

    INSERT INTO storage.objects (
        bucket_id, 
        name, 
        owner, 
        owner_id, 
        metadata, 
        version
    ) VALUES (
        src_bucket, 
        dest_name, 
        src_obj.owner, 
        src_obj.owner_id, 
        src_obj.metadata, 
        src_obj.version
    )
    ON CONFLICT DO NOTHING;

    DELETE FROM storage.objects
    WHERE bucket_id = src_bucket AND name = src_name;
END;
$$;



CREATE FUNCTION public.create_notification(user_id uuid, title text, message text, type public.notification_type DEFAULT 'info'::public.notification_type, data jsonb DEFAULT '{}'::jsonb) RETURNS uuid
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
    notification_id UUID;
BEGIN
    INSERT INTO public.notifications (user_id, title, message, type, data)
    VALUES (user_id, title, message, type, data)
    RETURNING id INTO notification_id;
    
    RETURN notification_id;
END;
$$;



CREATE FUNCTION public.current_user_team_ids() RETURNS SETOF uuid
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
  select team_id
  from public.team_members
  where user_id = auth.uid()
    and is_active = true
$$;



CREATE FUNCTION public.decline_team_invite(invite_id uuid) RETURNS void
    LANGUAGE sql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
  update public.team_invitations
  set status = 'declined', responded_at = now()
  where id = invite_id and invited_user_id = auth.uid() and status = 'pending';
$$;



CREATE FUNCTION public.delete_organization_safely(p_org_id uuid) RETURNS jsonb
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
DECLARE
  v_active_count INT;
BEGIN
  -- Check for active tournaments
  SELECT COUNT(*) INTO v_active_count
  FROM tournaments
  WHERE organizer_id = (SELECT owner_id FROM organizations WHERE id = p_org_id)
  AND status IN ('open', 'ongoing', 'check_in');

  IF v_active_count > 0 THEN
    RETURN jsonb_build_object('success', false, 'message', 'Cannot delete organization with active tournaments. Please complete or cancel them first.');
  END IF;

  -- Delete organization (Cascading deletes should handle children if configured, 
  -- otherwise we might need manual cleanup. Assuming FKs are set to CASCADE or we need to delete tournaments explicitly)
  
  -- Manual cleanup to be safe, assuming owner_id relates to auth.users or organizations table structure
  -- Note: existing code uses `organizations.owner_id` (User) -> `tournaments.organizer_id` (User).
  -- Wait, the organization entity might just be a profile.
  -- Let's check `tournaments` schema. `organizer_id` usually links to `auth.users` OR `organizations`.
  -- Code says: `from('tournaments').eq('organizer_id', user?.id)`
  -- So tournaments are linked to the USER, not the Organization ID directly?
  -- But `OrganizationSettings` fetches `organizations` by `owner_id`.
  -- If I delete the row in `organizations`, the user still exists.
  -- The user wants to delete "their organization account".
  -- This essentially means deleting the `organizations` row.
  
  DELETE FROM organizations WHERE id = p_org_id;
  
  RETURN jsonb_build_object('success', true, 'message', 'Organization deleted successfully.');
END;
$$;



CREATE FUNCTION public.end_expired_ghost_sessions() RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    AS $$
BEGIN
    UPDATE ghost_sessions
    SET ended_at = NOW()
    WHERE ended_at IS NULL
      AND expires_at < NOW();
END;
$$;



CREATE FUNCTION public.enforce_roster_member_limits() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
    v_limits record;
    v_role text;
    v_player_count int;
    v_coach_count int;
BEGIN
    IF TG_OP = 'INSERT' AND EXISTS (
        SELECT 1
        FROM public.team_roster_members trm
        WHERE trm.roster_id = NEW.roster_id
          AND trm.user_id = NEW.user_id
    ) THEN
        RETURN NEW;
    END IF;

    IF TG_OP = 'UPDATE'
       AND OLD.roster_role IS NOT DISTINCT FROM NEW.roster_role
       AND OLD.is_starter IS NOT DISTINCT FROM NEW.is_starter THEN
        RETURN NEW;
    END IF;

    v_role := COALESCE(
        NEW.roster_role::text,
        CASE WHEN COALESCE(NEW.is_starter, TRUE) THEN 'starter' ELSE 'substitute' END
    );

    SELECT *
    INTO v_limits
    FROM public.resolve_roster_capacity_limits(NEW.roster_id);

    IF v_role = 'coach' THEN
        IF NOT v_limits.allows_coaches THEN
            RAISE EXCEPTION 'This game mode does not allow coaches.';
        END IF;

        SELECT COUNT(*)
        INTO v_coach_count
        FROM public.team_roster_members trm
        WHERE trm.roster_id = NEW.roster_id
          AND COALESCE(
              trm.roster_role::text,
              CASE WHEN COALESCE(trm.is_starter, TRUE) THEN 'starter' ELSE 'substitute' END
          ) = 'coach'
          AND (TG_OP = 'INSERT' OR trm.user_id <> NEW.user_id);

        IF v_coach_count >= v_limits.max_coaches THEN
            RAISE EXCEPTION 'Coach limit reached (max %).', v_limits.max_coaches;
        END IF;

        RETURN NEW;
    END IF;

    SELECT COUNT(*)
    INTO v_player_count
    FROM public.team_roster_members trm
    WHERE trm.roster_id = NEW.roster_id
      AND COALESCE(
          trm.roster_role::text,
          CASE WHEN COALESCE(trm.is_starter, TRUE) THEN 'starter' ELSE 'substitute' END
      ) IN ('starter', 'substitute')
      AND (TG_OP = 'INSERT' OR trm.user_id <> NEW.user_id);

    IF v_player_count >= v_limits.max_players THEN
        RAISE EXCEPTION 'Player limit reached (max %).', v_limits.max_players;
    END IF;

    RETURN NEW;
END;
$$;



CREATE FUNCTION public.enroll_team_in_stage(p_stage_id uuid, p_team_id uuid) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
    -- Check if stage is locked
    IF EXISTS (SELECT 1 FROM tournament_stages WHERE id = p_stage_id AND is_locked = true) THEN
        RAISE EXCEPTION 'Stage is locked and cannot accept new enrollments';
    END IF;

    -- Check if already enrolled
    IF EXISTS (SELECT 1 FROM stage_participants WHERE stage_id = p_stage_id AND team_id = p_team_id) THEN
        RETURN;
    END IF;

    INSERT INTO stage_participants (stage_id, team_id)
    VALUES (p_stage_id, p_team_id);
END;
$$;



CREATE FUNCTION public.ensure_wallet(p_owner public.wallet_owner, p_owner_id uuid) RETURNS uuid
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE w_id UUID; BEGIN
  SELECT id INTO w_id FROM wallets WHERE owner_type=p_owner AND owner_id=p_owner_id;
  IF w_id IS NULL THEN
    INSERT INTO wallets(owner_type, owner_id) VALUES (p_owner, p_owner_id) RETURNING id INTO w_id;
  END IF;
  RETURN w_id;
END; $$;



CREATE FUNCTION public.expire_ghost_approvals() RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    AS $$
BEGIN
    UPDATE ghost_approvals
    SET status = 'expired'
    WHERE status = 'pending'
      AND created_at < NOW() - INTERVAL '24 hours';

    UPDATE ghost_approvals
    SET status = 'expired'
    WHERE status = 'approved'
      AND expires_at IS NOT NULL
      AND expires_at < NOW();
END;
$$;





CREATE TABLE public.venues (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    name text NOT NULL,
    description text,
    address text NOT NULL,
    city text NOT NULL,
    state text,
    country text NOT NULL,
    postal_code text,
    latitude numeric(10,8),
    longitude numeric(11,8),
    capacity integer,
    amenities text[] DEFAULT '{}'::text[],
    equipment text[] DEFAULT '{}'::text[],
    operating_hours jsonb DEFAULT '{}'::jsonb,
    contact_phone text,
    contact_email text,
    website_url text,
    social_media jsonb DEFAULT '{}'::jsonb,
    images text[] DEFAULT '{}'::text[],
    is_active boolean DEFAULT true,
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    owner_id uuid,
    deleted_at timestamp with time zone,
    games text,
    stations integer,
    hours text,
    pc_specs jsonb DEFAULT '{}'::jsonb,
    slug text,
    card_image text,
    venue_id text,
    status text DEFAULT 'draft'::text NOT NULL,
    rejection_reason text,
    reviewed_by uuid,
    reviewed_at timestamp with time zone,
    submitted_at timestamp with time zone,
    published_at timestamp with time zone,
    subscription_tier text DEFAULT 'free'::text NOT NULL,
    desktop_pairing_token text,
    price_per_hour numeric(10,2) DEFAULT 0,
    currency text DEFAULT 'USD'::text,
    opening_hours jsonb DEFAULT '{}'::jsonb,
    hub_api_key_hash text,
    hub_key_issued_at timestamp with time zone,
    hub_key_rotated_at timestamp with time zone,
    hub_lan_url text,
    hub_version text,
    hub_last_heartbeat timestamp with time zone,
    organization_id uuid,
    CONSTRAINT venues_status_check CHECK ((status = ANY (ARRAY['draft'::text, 'pending_review'::text, 'published'::text, 'rejected'::text, 'suspended'::text, 'archived'::text]))),
    CONSTRAINT venues_tier_check CHECK ((subscription_tier = ANY (ARRAY['free'::text, 'basic'::text, 'pro'::text, 'enterprise'::text])))
);



CREATE FUNCTION public.find_nearby_venues(user_lat double precision, user_lng double precision, radius_km double precision DEFAULT 50) RETURNS SETOF public.venues
    LANGUAGE sql STABLE
    AS $$
  SELECT v.*
  FROM public.venues v
  WHERE
    v.status = 'published'
    AND v.deleted_at IS NULL
    AND v.latitude  IS NOT NULL
    AND v.longitude IS NOT NULL
    AND (
      6371 * acos(
        LEAST(1.0,
          cos(radians(user_lat)) * cos(radians(v.latitude)) *
          cos(radians(v.longitude) - radians(user_lng)) +
          sin(radians(user_lat)) * sin(radians(v.latitude))
        )
      )
    ) <= radius_km
  ORDER BY (
      6371 * acos(
        LEAST(1.0,
          cos(radians(user_lat)) * cos(radians(v.latitude)) *
          cos(radians(v.longitude) - radians(user_lng)) +
          sin(radians(user_lat)) * sin(radians(v.latitude))
        )
      )
    ) ASC;
$$;



CREATE FUNCTION public.fix_match_veto_state(p_veto_id uuid, p_team1_id uuid, p_team2_id uuid) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
  v_veto_record record;
  v_is_authorized boolean;
BEGIN
  -- Fetch current veto state
  SELECT * INTO v_veto_record
  FROM public.valorant_match_map_vetos
  WHERE id = p_veto_id;

  IF NOT FOUND THEN
    RAISE EXCEPTION 'Veto not found';
  END IF;

  -- Security Check: Caller must be captain/owner of p_team1_id or p_team2_id OR organizer
  SELECT EXISTS (
    SELECT 1 FROM public.team_members
    WHERE team_id IN (p_team1_id, p_team2_id)
    AND user_id = auth.uid()
    AND role IN ('captain', 'owner')
    AND is_active = true
  ) OR EXISTS (
    SELECT 1 FROM public.teams
    WHERE id IN (p_team1_id, p_team2_id)
    AND owner_id = auth.uid()
  ) OR EXISTS (
    SELECT 1 FROM public.tournaments t
    WHERE t.id = v_veto_record.tournament_id
    AND t.organizer_id = auth.uid()
  ) INTO v_is_authorized;

  IF NOT v_is_authorized THEN
    RAISE EXCEPTION 'Unauthorized to fix veto state';
  END IF;

  -- Update IDs
  UPDATE public.valorant_match_map_vetos
  SET 
    team1_id = p_team1_id,
    team2_id = p_team2_id,
    -- Fix current_team_id
    current_team_id = CASE 
      -- If it matches old Team 1, update to new Team 1
      WHEN current_team_id = v_veto_record.team1_id THEN p_team1_id
      -- If it matches old Team 2, update to new Team 2
      WHEN current_team_id = v_veto_record.team2_id THEN p_team2_id
      -- If status is pending, always reset to Team 1 (start of veto)
      WHEN status = 'pending' THEN p_team1_id
      -- If null, default to Team 1
      WHEN current_team_id IS NULL THEN p_team1_id
      -- Otherwise keep it (though if it was stale and didn't match above, it might still be broken)
      ELSE current_team_id 
    END,
    -- Fix current_action if status is pending and it's not 'ban'
    current_action = CASE
      WHEN status = 'pending' AND (current_action IS NULL OR current_action != 'ban') THEN 'ban'
      ELSE current_action
    END,
    -- Fix current_action_number if status is pending and it's not 1
    current_action_number = CASE
      WHEN status = 'pending' AND current_action_number != 1 THEN 1
      ELSE current_action_number
    END
  WHERE id = p_veto_id;
  
END;
$$;



CREATE FUNCTION public.fn_aggregate_sponsor_impression() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
    INSERT INTO daily_sponsor_stats (sponsor_id, stat_date, impressions, clicks, unique_impressions)
    VALUES (
        NEW.sponsor_id,
        CURRENT_DATE,
        CASE WHEN NEW.event_type = 'impression' THEN 1 ELSE 0 END,
        CASE WHEN NEW.event_type = 'click' THEN 1 ELSE 0 END,
        CASE WHEN NEW.event_type = 'impression' AND NEW.visitor_id IS NOT NULL THEN 1 ELSE 0 END
    )
    ON CONFLICT (sponsor_id, stat_date)
    DO UPDATE SET
        impressions = daily_sponsor_stats.impressions
            + CASE WHEN NEW.event_type = 'impression' THEN 1 ELSE 0 END,
        clicks = daily_sponsor_stats.clicks
            + CASE WHEN NEW.event_type = 'click' THEN 1 ELSE 0 END,
        unique_impressions = daily_sponsor_stats.unique_impressions
            + CASE WHEN NEW.event_type = 'impression' AND NEW.visitor_id IS NOT NULL THEN 1 ELSE 0 END;

    RETURN NEW;
END;
$$;



CREATE FUNCTION public.fn_sync_admin_profile() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
  target_user_id UUID;
  role_array     TEXT[];
BEGIN
  IF TG_OP = 'DELETE' THEN
    target_user_id := OLD.user_id;
  ELSE
    target_user_id := NEW.user_id;
  END IF;

  SELECT ARRAY_AGG(ar.key)
    INTO role_array
    FROM admin_user_roles aur
    JOIN admin_roles ar ON ar.id = aur.role_id
   WHERE aur.user_id = target_user_id;

  UPDATE profiles
     SET admin_roles = COALESCE(role_array, '{}'),
         is_admin    = (role_array IS NOT NULL AND array_length(role_array, 1) > 0)
   WHERE id = target_user_id;

  RETURN COALESCE(NEW, OLD);
END;
$$;



CREATE FUNCTION public.fn_sync_tournament_organizer() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    AS $$
BEGIN
    IF NEW.organization_id IS NOT NULL THEN
        SELECT owner_id INTO NEW.organizer_id
        FROM public.organizations
        WHERE id = NEW.organization_id;
    END IF;
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.forfeit_match(p_match_id uuid, p_forfeiting_team_id uuid, p_winning_team_id uuid, p_reason text DEFAULT 'Auto-Forfeit: Missed Check-in'::text) RETURNS void
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
BEGIN
  -- Update match status and winner
  UPDATE brkt_matches
  SET 
    status = 'completed',
    winner_id = p_winning_team_id,
    loser_id = p_forfeiting_team_id,
    end_time = NOW(),
    metadata = jsonb_set(COALESCE(metadata, '{}'), '{forfeit_reason}', to_jsonb(p_reason))
  WHERE id = p_match_id;
END;
$$;



CREATE FUNCTION public.forfeit_match(p_match_id uuid, p_forfeiting_team_id uuid, p_winning_team_id uuid, p_reason text, p_winner_score integer, p_loser_score integer) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
    v_team1_id UUID;
    v_team2_id UUID;
BEGIN
    -- Get match details
    SELECT team1_id, team2_id INTO v_team1_id, v_team2_id
    FROM brkt_matches
    WHERE id = p_match_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Match not found';
    END IF;

    -- Update match
    UPDATE brkt_matches
    SET 
        status = 'completed',
        winner_id = p_winning_team_id,
        loser_id = p_forfeiting_team_id,
        team1_score = CASE WHEN team1_id = p_winning_team_id THEN p_winner_score ELSE p_loser_score END,
        team2_score = CASE WHEN team2_id = p_winning_team_id THEN p_winner_score ELSE p_loser_score END,
        result_notes = p_reason,
        updated_at = NOW()
    WHERE id = p_match_id;
    
    -- We intentionally do NOT update tournament_participants status here.
    -- This ensures the team is kept in the tournament (just with a loss).
END;
$$;



CREATE FUNCTION public.generate_dummy_teams(target_tournament_id uuid, count integer) RETURNS void
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
DECLARE
    i INT;
    new_team_id UUID;
BEGIN
    FOR i IN 1..count LOOP
        INSERT INTO teams (name, tag, game, owner_id)
        VALUES ('Test Team ' || i, 'TT' || i, 'Organization', 'f0835b7f-cf1c-4cca-8437-017f3448aa50')
        RETURNING id INTO new_team_id;

        INSERT INTO tournament_participants (tournament_id, team_id, status, participant_type)
        VALUES (target_tournament_id, new_team_id, 'checked_in', 'team');
    END LOOP;
END;
$$;



CREATE FUNCTION public.generate_license_id() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
  prefix TEXT;
  candidate TEXT;
BEGIN
  IF NEW.license_id IS NULL OR NEW.license_id = '' THEN
    prefix := CASE NEW.license_type
      WHEN 'venue_owner'  THEN 'ESP-VO-'
      WHEN 'organizer'    THEN 'ESP-OR-'
      WHEN 'broadcaster'  THEN 'ESP-BC-'
      ELSE 'ESP-XX-'
    END;
    LOOP
      candidate := prefix || LPAD((FLOOR(RANDOM() * 900000) + 100000)::TEXT, 6, '0');
      EXIT WHEN NOT EXISTS (SELECT 1 FROM public.licenses WHERE license_id = candidate);
    END LOOP;
    NEW.license_id := candidate;
  END IF;
  RETURN NEW;
END;
$$;



CREATE FUNCTION public.generate_pairing_token() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
  chars TEXT := 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  candidate TEXT;
  i INTEGER;
BEGIN
  IF NEW.desktop_pairing_token IS NULL THEN
    LOOP
      candidate := '';
      FOR i IN 1..6 LOOP
        candidate := candidate || SUBSTR(chars, FLOOR(RANDOM() * LENGTH(chars) + 1)::INT, 1);
      END LOOP;
      EXIT WHEN NOT EXISTS (SELECT 1 FROM public.venues WHERE desktop_pairing_token = candidate);
    END LOOP;
    NEW.desktop_pairing_token := candidate;
  END IF;
  RETURN NEW;
END;
$$;



CREATE FUNCTION public.generate_profile_license_id() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    AS $$
BEGIN
  -- We only care about approved and active business roles (organizer or venue_owner)
  IF NEW.status = 'approved' AND NEW.is_active = true AND NEW.role IN ('organizer', 'venue_owner') THEN
    -- Check if the user already has a license_id
    IF NOT EXISTS (SELECT 1 FROM public.profiles WHERE id = NEW.user_id AND license_id IS NOT NULL) THEN
      -- Automatically assign a new license ID
      UPDATE public.profiles
      SET license_id = gen_random_uuid()
      WHERE id = NEW.user_id AND license_id IS NULL;
    END IF;
  END IF;
  
  RETURN NEW;
END;
$$;



CREATE FUNCTION public.generate_stage_bracket(p_stage_id uuid) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
    v_tournament_id UUID;
    v_format TEXT;
    v_capacity INTEGER;
    v_num_rounds INTEGER;
    v_num_matches INTEGER;
    v_round INTEGER;
    v_match_num INTEGER;
    v_lb_rounds INTEGER;
    v_gf_id UUID;
BEGIN
    -- Get stage info
    SELECT tournament_id, format, capacity
    INTO v_tournament_id, v_format, v_capacity
    FROM tournament_stages
    WHERE id = p_stage_id;

    IF v_tournament_id IS NULL THEN
        RAISE EXCEPTION 'Stage not found: %', p_stage_id;
    END IF;

    IF v_format IS NULL OR v_format = '' THEN
        v_format := 'single_elimination';
    END IF;

    v_num_rounds := CEIL(LOG(2, GREATEST(v_capacity, 2)));

    DELETE FROM tournament_matches WHERE stage_id = p_stage_id;

    IF v_format = 'single_elimination' THEN
        FOR v_round IN 1..v_num_rounds LOOP
            v_num_matches := POWER(2, v_num_rounds - v_round)::INTEGER;
            FOR v_match_num IN 1..v_num_matches LOOP
                INSERT INTO tournament_matches (id, tournament_id, stage_id, round, match_number, status, bracket_side) 
                VALUES (gen_random_uuid(), v_tournament_id, p_stage_id, v_round, v_match_num, 'pending', 'winners');
            END LOOP;
        END LOOP;
        
    ELSIF v_format = 'double_elimination' THEN
        FOR v_round IN 1..v_num_rounds LOOP
            v_num_matches := POWER(2, v_num_rounds - v_round)::INTEGER;
            FOR v_match_num IN 1..v_num_matches LOOP
                INSERT INTO tournament_matches (id, tournament_id, stage_id, round, match_number, status, bracket_side) 
                VALUES (gen_random_uuid(), v_tournament_id, p_stage_id, v_round, v_match_num, 'pending', 'winners');
            END LOOP;
        END LOOP;
        
        v_lb_rounds := 2 * (v_num_rounds - 1);
        FOR v_round IN 1..v_lb_rounds LOOP
            IF v_round % 2 = 1 THEN
                v_num_matches := POWER(2, v_num_rounds - CEIL(v_round::FLOAT / 2) - 1)::INTEGER;
            ELSE
                v_num_matches := POWER(2, v_num_rounds - (v_round / 2) - 1)::INTEGER;
            END IF;
            v_num_matches := GREATEST(v_num_matches, 1);
            
            FOR v_match_num IN 1..v_num_matches LOOP
                INSERT INTO tournament_matches (id, tournament_id, stage_id, round, match_number, status, bracket_side) 
                VALUES (gen_random_uuid(), v_tournament_id, p_stage_id, v_round, v_match_num, 'pending', 'losers');
            END LOOP;
        END LOOP;
        
        v_gf_id := gen_random_uuid();
        INSERT INTO tournament_matches (id, tournament_id, stage_id, round, match_number, status, bracket_side) 
        VALUES (v_gf_id, v_tournament_id, p_stage_id, v_num_rounds + 1, 1, 'pending', 'final');
    ELSE
        RAISE EXCEPTION 'Unsupported format: %', v_format;
    END IF;

    -- Step 1: Seed teams from tournament_participants into stage_participants
    PERFORM public.seed_stage_from_registrations(p_stage_id);
    
    -- Step 2: Assign teams from stage_participants to Round 1 matches
    PERFORM public.assign_teams_to_bracket(p_stage_id);
END;
$$;



CREATE FUNCTION public.generate_venue_hub_key() RETURNS text
    LANGUAGE sql
    SET search_path TO 'public'
    AS $$
  SELECT encode(extensions.gen_random_bytes(16), 'hex');
$$;



CREATE FUNCTION public.generate_venue_id() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
  candidate TEXT;
BEGIN
  IF NEW.venue_id IS NULL THEN
    LOOP
      candidate := 'VEN-' || LPAD((FLOOR(RANDOM() * 900000) + 100000)::TEXT, 6, '0');
      EXIT WHEN NOT EXISTS (SELECT 1 FROM public.venues WHERE venue_id = candidate);
    END LOOP;
    NEW.venue_id := candidate;
  END IF;
  RETURN NEW;
END;
$$;



CREATE FUNCTION public.get_admin_dashboard_stats() RETURNS jsonb
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
    result JSONB;
BEGIN
    SELECT json_build_object(
        'totalUsers', (SELECT count(*) FROM profiles),
        'activeVenues', (SELECT count(*) FROM venues),
        'activeTournaments', (SELECT count(*) FROM tournaments),
        'pendingVerifications', (SELECT count(*) FROM verification_requests WHERE status = 'pending'),
        'totalBookings', (SELECT count(*) FROM venue_bookings),
        'totalRevenue', (
            SELECT COALESCE(SUM(prize_pool), 0)
            FROM tournaments
            WHERE status != 'cancelled'
        ),
        'newUsersToday', (
            SELECT count(*)
            FROM profiles
            WHERE created_at >= NOW() - INTERVAL '24 hours'
        ),
        'pendingPartners', (SELECT count(*) FROM partner_applications WHERE status = 'pending')
    )::jsonb INTO result;

    RETURN result;
END;
$$;



CREATE FUNCTION public.get_organizer_tournaments_with_counts(p_organizer_id uuid) RETURNS TABLE(id uuid, name text, game text, start_date timestamp with time zone, end_date timestamp with time zone, venue_id uuid, max_teams integer, prize_pool numeric, organizer_id uuid, entry_fee numeric, is_public boolean, banner_url text, logo_url text, slug text, description text, deleted_at timestamp with time zone, current_participants bigint, status text)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
begin
  return query
  select 
    t.id,
    t.name,
    t.game,
    t.start_date,
    t.end_date,
    t.venue_id,
    t.max_teams,
    t.prize_pool,
    t.organizer_id,
    t.entry_fee,
    t.is_public,
    t.banner_url,
    t.logo_url,
    t.slug,
    t.description,
    t.deleted_at,
    (select count(*)::bigint from tournament_participants tp where tp.tournament_id = t.id) as current_participants,
    t.status::text
  from tournaments t
  where (
    t.organizer_id = p_organizer_id
    or exists (
      select 1 from tournament_staff ts 
      where ts.tournament_id = t.id 
      and ts.user_id = p_organizer_id
      and ts.status = 'active'
    )
  )
  and t.deleted_at is null
  order by t.start_date asc;
end;
$$;



CREATE FUNCTION public.get_roster_members(p_team_id uuid) RETURNS TABLE(user_id uuid, role text, username text, avatar_url text, riot_tag text, card_image_url text)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
    RETURN QUERY
    SELECT
        tm.user_id,
        tm.role,
        p.username,
        p.avatar_url,
        p.riot_tag,
        p.card_image_url
    FROM team_members tm
    JOIN profiles p ON p.id = tm.user_id
    WHERE tm.team_id = p_team_id AND tm.is_active = true
    ORDER BY tm.display_order, tm.role, p.username;
END;
$$;



CREATE FUNCTION public.get_stage_best_of(p_stage_id uuid) RETURNS integer
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
DECLARE
    v_best_of INTEGER;
BEGIN
    SELECT COALESCE(
        (config->>'bestOf')::int,
        (config->'veto'->>'best_of')::int,
        1
    ) INTO v_best_of
    FROM tournament_stages
    WHERE id = p_stage_id;
    
    RETURN COALESCE(v_best_of, 1);
END;
$$;



CREATE FUNCTION public.get_stage_teams(p_stage_id uuid) RETURNS TABLE(team_id uuid, name text, logo_url text)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
    RETURN QUERY
    SELECT t.id, t.name, t.logo_url
    FROM stage_participants sp
    JOIN teams t ON t.id = sp.team_id
    WHERE sp.stage_id = p_stage_id;
END;
$$;



CREATE FUNCTION public.get_team_members(t_id uuid) RETURNS TABLE(user_id uuid, username text, email text, avatar_url text, card_image_url text, role text, joined_at timestamp with time zone, is_active boolean)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
begin
  -- security check: only allow if caller is owner or member
  if exists (select 1 from public.teams t where t.id = t_id and t.owner_id = auth.uid())
     or exists (select 1 from public.team_members tm where tm.team_id = t_id and tm.user_id = auth.uid() and tm.is_active = true)
  then
    return query
      select 
        p.id as user_id, 
        p.username, 
        p.email, 
        p.avatar_url,
        p.card_image_url, 
        tm.role::text, 
        tm.joined_at, 
        tm.is_active
      from public.team_members tm
      join public.profiles p on p.id = tm.user_id
      where tm.team_id = t_id
        and tm.is_active = true;
  else
    -- Return empty set if not authorized
    return;
  end if;
end;
$$;



CREATE FUNCTION public.get_team_roster(t_id uuid) RETURNS TABLE(user_id uuid, username text, full_name text)
    LANGUAGE sql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
  select tm.user_id, p.username, p.full_name
  from public.team_members tm
  join public.profiles p on p.id = tm.user_id
  where tm.team_id = t_id and (tm.is_active is distinct from false);
$$;



CREATE FUNCTION public.get_tournament_participant_count(t_id uuid) RETURNS integer
    LANGUAGE sql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
  select count(*)::int from public.tournament_participants where tournament_id = t_id;
$$;



CREATE FUNCTION public.get_tournament_registration_count(tournament_uuid uuid) RETURNS integer
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
BEGIN
    RETURN (
        SELECT COUNT(*)::INTEGER 
        FROM public.tournament_participants 
        WHERE tournament_id = tournament_uuid 
        AND status IN ('pending', 'approved', 'checked_in')
    );
END;
$$;



CREATE FUNCTION public.get_user_permissions(p_user_id uuid) RETURNS TABLE(permission text, resource text, action text)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
  RETURN QUERY
  SELECT DISTINCT rp.permission, rp.resource, rp.action
  FROM public.user_roles ur
  JOIN public.role_permissions rp ON ur.role = rp.role
  WHERE ur.user_id = p_user_id 
    AND ur.is_active = true
  ORDER BY rp.resource, rp.action;
END;
$$;



CREATE FUNCTION public.get_user_roles(p_user_id uuid) RETURNS TABLE(role character varying, is_active boolean, assigned_at timestamp with time zone)
    LANGUAGE sql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
    SELECT ur.role, ur.is_active, ur.assigned_at
    FROM public.user_roles ur
    WHERE ur.user_id = p_user_id AND ur.is_active = TRUE
    ORDER BY ur.assigned_at DESC;
$$;



CREATE FUNCTION public.get_user_tournament_registrations(user_uuid uuid) RETURNS TABLE(id uuid, tournament_id uuid, tournament_name text, registration_type public.registration_type, status public.registration_status, registration_date timestamp with time zone, gamer_tag text, team_name text)
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
BEGIN
    RETURN QUERY
    SELECT 
        tp.id,
        tp.tournament_id,
        t.name as tournament_name,
        tp.registration_type,
        tp.status,
        tp.registration_date,
        tp.gamer_tag,
        tp.team_name
    FROM public.tournament_participants tp
    JOIN public.tournaments t ON tp.tournament_id = t.id
    WHERE 
        (tp.registration_type = 'solo' AND tp.user_id = user_uuid) OR
        (tp.registration_type = 'team' AND tp.team_captain_id = user_uuid)
    ORDER BY tp.registration_date DESC;
END;
$$;



CREATE FUNCTION public.get_verification_request_basic(request_id uuid) RETURNS TABLE(id uuid, user_id uuid, requested_role text, status text, created_at timestamp with time zone)
    LANGUAGE sql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
  SELECT 
    vr.id,
    vr.user_id,
    vr.requested_role,
    vr.status,
    vr.created_at
  FROM verification_requests vr
  WHERE vr.id = request_id;
$$;



CREATE FUNCTION public.get_verification_request_with_data(request_id uuid) RETURNS TABLE(id uuid, user_id uuid, requested_role text, status text, first_name text, last_name text, business_name text, business_type text, contact_email text, created_at timestamp with time zone, organizer_data jsonb, venue_data jsonb, venue_images jsonb)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
  has_contact_email BOOLEAN;
BEGIN
  -- Check if contact_email column exists
  SELECT EXISTS (
    SELECT 1 FROM information_schema.columns 
    WHERE table_name = 'verification_requests' 
    AND column_name = 'contact_email'
  ) INTO has_contact_email;

  -- Return data based on what columns exist
  IF has_contact_email THEN
    RETURN QUERY
    SELECT 
      vr.id,
      vr.user_id,
      vr.requested_role,
      vr.status,
      vr.first_name,
      vr.last_name,
      vr.business_name,
      vr.business_type,
      vr.contact_email,
      vr.created_at,
      vr.organizer_data,
      vr.venue_data,
      vr.venue_images
    FROM verification_requests vr
    WHERE vr.id = request_id;
  ELSE
    RETURN QUERY
    SELECT 
      vr.id,
      vr.user_id,
      vr.requested_role,
      vr.status,
      vr.first_name,
      vr.last_name,
      vr.business_name,
      vr.business_type,
      ''::TEXT as contact_email,  -- Default empty string
      vr.created_at,
      vr.organizer_data,
      vr.venue_data,
      vr.venue_images
    FROM verification_requests vr
    WHERE vr.id = request_id;
  END IF;
END;
$$;



CREATE FUNCTION public.get_verified_roles(p_user_id uuid) RETURNS TABLE(role character varying, status character varying, verified_at timestamp with time zone)
    LANGUAGE sql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
    SELECT vr.role, vr.status, vr.verified_at
    FROM public.verified_roles vr
    WHERE vr.user_id = p_user_id AND vr.status = 'approved'
    ORDER BY vr.verified_at DESC;
$$;



CREATE FUNCTION public.handle_new_sponsor_interaction() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
BEGIN
    INSERT INTO public.daily_sponsor_stats (sponsor_id, stat_date, impressions, clicks)
    VALUES (
        NEW.sponsor_id, 
        CURRENT_DATE, 
        CASE WHEN NEW.event_type = 'impression' THEN 1 ELSE 0 END,
        CASE WHEN NEW.event_type = 'click' THEN 1 ELSE 0 END
    )
    ON CONFLICT (sponsor_id, stat_date)
    DO UPDATE SET
        impressions = daily_sponsor_stats.impressions + EXCLUDED.impressions,
        clicks = daily_sponsor_stats.clicks + EXCLUDED.clicks;
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.handle_new_user() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
    INSERT INTO public.profiles (id, username, full_name, email, avatar_url, role, date_of_birth)
    VALUES (
        NEW.id,
        COALESCE(NEW.raw_user_meta_data->>'username', split_part(NEW.email, '@', 1)),
        COALESCE(NEW.raw_user_meta_data->>'full_name', split_part(NEW.email, '@', 1)),
        NEW.email,
        NEW.raw_user_meta_data->>'avatar_url',
        'casual'::app_role,
        (NEW.raw_user_meta_data->>'date_of_birth')::date
    );
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.handle_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
BEGIN
    NEW.updated_at = now();
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.has_admin_permission(permission_name text) RETURNS boolean
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
    RETURN EXISTS (
        SELECT 1 FROM public.profiles p
        WHERE p.id = auth.uid() 
        AND p.is_admin = true
        AND (
            'super_admin' = ANY(p.admin_roles) OR
            permission_name = ANY(p.admin_permissions)
        )
    );
END;
$$;



CREATE FUNCTION public.has_super_admin_role() RETURNS boolean
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
  RETURN EXISTS (
    SELECT 1
    FROM admin_user_roles aur
    JOIN admin_roles ar ON ar.id = aur.role_id
    WHERE aur.user_id = auth.uid()
    AND lower(ar.name) = 'super_admin'
  );
END;
$$;



CREATE FUNCTION public.has_verified_role(role_name public.app_role) RETURNS boolean
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
    RETURN EXISTS (
        SELECT 1 FROM public.verified_roles vr
        WHERE vr.user_id = auth.uid() 
        AND vr.role = role_name 
        AND vr.is_active = true
        AND (vr.expires_at IS NULL OR vr.expires_at > NOW())
    );
END;
$$;



CREATE FUNCTION public.initialize_match_veto(p_match_id uuid) RETURNS uuid
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
  v_veto_id uuid;
  v_match_record record;
  v_best_of integer;
BEGIN
  -- Get match details and stage best_of from column (not JSONB)
  SELECT 
    tm.tournament_id, 
    tm.team1_id, 
    tm.team2_id, 
    tm.stage_id,
    ts.best_of  -- Now reading from column
  INTO v_match_record
  FROM public.tournament_matches tm
  LEFT JOIN public.tournament_stages ts ON ts.id = tm.stage_id
  WHERE tm.id = p_match_id;
  
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Match not found';
  END IF;
  
  -- Use stage best_of column (trigger will sync it anyway, but be explicit)
  v_best_of := COALESCE(v_match_record.best_of, 1);
  
  -- Check if veto already exists
  SELECT id INTO v_veto_id
  FROM public.match_map_vetos
  WHERE match_id = p_match_id;
  
  IF FOUND THEN
    -- Update existing veto with stage_id (trigger will sync best_of)
    UPDATE public.match_map_vetos
    SET
      stage_id = v_match_record.stage_id,
      team1_link_token = COALESCE(team1_link_token, encode(gen_random_bytes(16), 'hex')),
      team2_link_token = COALESCE(team2_link_token, encode(gen_random_bytes(16), 'hex'))
    WHERE id = v_veto_id;
    
    RETURN v_veto_id;
  END IF;
  
  -- Create new veto with stage_id (trigger will auto-set best_of from stage)
  INSERT INTO public.match_map_vetos (
    match_id,
    tournament_id,
    stage_id,  -- New foreign key
    team1_id,
    team2_id,
    best_of,  -- Will be overwritten by trigger but set explicitly too
    status,
    current_team_id,
    current_action,
    current_action_number,
    turn_started_at,
    started_at,
    team1_link_token,
    team2_link_token
  ) VALUES (
    p_match_id,
    v_match_record.tournament_id,
    v_match_record.stage_id,  -- Set stage_id, trigger syncs best_of
    v_match_record.team1_id,
    v_match_record.team2_id,
    v_best_of,
    'in_progress',
    v_match_record.team1_id,
    'ban',
    1,
    NOW(),
    NOW(),
    encode(gen_random_bytes(16), 'hex'),
    encode(gen_random_bytes(16), 'hex')
  )
  RETURNING id INTO v_veto_id;
  
  RETURN v_veto_id;
END;
$$;



CREATE FUNCTION public.initialize_match_veto(p_match_id uuid, p_veto_format text DEFAULT 'standard_7'::text) RETURNS uuid
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
  v_veto_id uuid;
  v_match_record record;
  v_best_of integer;
BEGIN
  -- Derive best_of from format
  v_best_of := CASE 
    WHEN p_veto_format ILIKE '%bo1%' THEN 1
    WHEN p_veto_format ILIKE '%bo3%' THEN 3
    WHEN p_veto_format ILIKE '%bo5%' THEN 5
    WHEN p_veto_format = 'standard_7' THEN 1
    ELSE 1
  END;

  -- Get match details
  SELECT tournament_id, team1_id, team2_id
  INTO v_match_record
  FROM public.tournament_matches
  WHERE id = p_match_id;
  
  IF NOT FOUND THEN
    SELECT v.tournament_id, m.team1_id, m.team2_id
    INTO v_match_record
    FROM public.brkt_matches m
    JOIN public.brkt_versions v ON m.version_id = v.id
    WHERE m.id = p_match_id;
    
    IF NOT FOUND THEN
      RAISE EXCEPTION 'Match not found';
    END IF;
  END IF;
  
  -- Check existing
  SELECT id INTO v_veto_id
  FROM public.match_map_vetos
  WHERE match_id = p_match_id;
  
  IF FOUND THEN
    UPDATE public.match_map_vetos
    SET
      team1_link_token = COALESCE(team1_link_token, encode(gen_random_bytes(16), 'hex')),
      team2_link_token = COALESCE(team2_link_token, encode(gen_random_bytes(16), 'hex')),
      best_of = COALESCE(best_of, v_best_of)
    WHERE id = v_veto_id;
    
    RETURN v_veto_id;
  END IF;
  
  INSERT INTO public.match_map_vetos (
    match_id,
    tournament_id,
    team1_id,
    team2_id,
    veto_format,
    best_of,
    status,
    current_team_id,
    current_action,
    current_action_number,
    turn_started_at,
    started_at,
    team1_link_token,
    team2_link_token,
    team1_picked_maps,
    team2_picked_maps
  ) VALUES (
    p_match_id,
    v_match_record.tournament_id,
    v_match_record.team1_id,
    v_match_record.team2_id,
    p_veto_format,
    v_best_of,
    'in_progress', -- Start as in_progress if we have a default best_of
    v_match_record.team1_id,
    'ban',
    1,
    NOW(),
    NOW(),
    encode(gen_random_bytes(16), 'hex'),
    encode(gen_random_bytes(16), 'hex'),
    '[]'::jsonb,
    '[]'::jsonb
  )
  RETURNING id INTO v_veto_id;
  
  RETURN v_veto_id;
END;
$$;



CREATE FUNCTION public.is_admin() RETURNS boolean
    LANGUAGE sql STABLE SECURITY DEFINER
    AS $$
  SELECT EXISTS (
    SELECT 1 FROM profiles 
    WHERE id = auth.uid() AND (is_admin = true OR role = 'admin')
  );
$$;



CREATE FUNCTION public.is_admin_user(p_user_id uuid) RETURNS boolean
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
  RETURN EXISTS (
    SELECT 1 
    FROM public.profiles 
    WHERE id = p_user_id 
    AND is_admin = TRUE
  );
END;
$$;



CREATE FUNCTION public.is_org_active_staff(_organization_id uuid) RETURNS boolean
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
    RETURN EXISTS (
        SELECT 1
        FROM public.organization_staff
        WHERE organization_id = _organization_id
          AND user_id = auth.uid()
          AND status = 'active'
    );
END;
$$;



CREATE FUNCTION public.is_org_admin(_organization_id uuid) RETURNS boolean
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
    SELECT EXISTS (
        SELECT 1
        FROM public.organization_staff
        WHERE organization_id = _organization_id
          AND user_id = auth.uid()
          AND role = 'admin'
          AND status = 'active'
    );
$$;



CREATE FUNCTION public.is_org_staff_user(_org_staff_id uuid) RETURNS boolean
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
    RETURN EXISTS (
        SELECT 1
        FROM public.organization_staff
        WHERE id = _org_staff_id
          AND user_id = auth.uid()
    );
END;
$$;



CREATE FUNCTION public.is_team_owner(tid uuid) RETURNS boolean
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
  select exists (
    select 1 from public.teams t
    where t.id = tid and t.owner_id = auth.uid()
  );
$$;



CREATE FUNCTION public.is_user_registered_for_tournament(tournament_uuid uuid, user_uuid uuid) RETURNS boolean
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
BEGIN
    RETURN EXISTS (
        SELECT 1 
        FROM public.tournament_participants 
        WHERE tournament_id = tournament_uuid 
        AND (
            (registration_type = 'solo' AND user_id = user_uuid) OR
            (registration_type = 'team' AND team_captain_id = user_uuid)
        )
        AND status IN ('pending', 'approved', 'checked_in')
    );
END;
$$;



CREATE FUNCTION public.is_venue_open(p_venue_id uuid, p_at timestamp with time zone DEFAULT now()) RETURNS boolean
    LANGUAGE plpgsql STABLE
    AS $$
DECLARE
  v_hours JSONB;
  v_day TEXT;
  v_open TIME;
  v_close TIME;
BEGIN
  SELECT opening_hours INTO v_hours FROM venues WHERE id = p_venue_id;
  IF v_hours IS NULL OR v_hours = '{}'::jsonb THEN RETURN TRUE; END IF;
  v_day := LOWER(TO_CHAR(p_at AT TIME ZONE 'UTC', 'Dy'));
  IF NOT v_hours ? v_day THEN RETURN FALSE; END IF;
  v_open  := (v_hours -> v_day ->> 'open')::TIME;
  v_close := (v_hours -> v_day ->> 'close')::TIME;
  RETURN (p_at AT TIME ZONE 'UTC')::TIME BETWEEN v_open AND v_close;
END;
$$;



CREATE FUNCTION public.normalize_booking_code() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
  IF NEW.booking_code IS NOT NULL THEN
    NEW.booking_code := UPPER(TRIM(NEW.booking_code));
  END IF;
  RETURN NEW;
END;
$$;



CREATE FUNCTION public.partner_auth_has_password(target_user_id uuid) RETURNS boolean
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'auth'
    AS $$
    SELECT COALESCE(LENGTH(users.encrypted_password), 0) > 0
    FROM auth.users AS users
    WHERE users.id = target_user_id;
$$;



CREATE FUNCTION public.proc_advance_bracket_match() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    AS $$
DECLARE
    v_version_id UUID;
    v_edge RECORD;
    v_update_field TEXT;
BEGIN
    -- Only process if status is 'pending' (default)
    IF NEW.status != 'pending' THEN
        RETURN NEW;
    END IF;

    -- 1. Get the match's version_id
    SELECT version_id INTO v_version_id
    FROM public.brkt_matches
    WHERE id = NEW.match_id;

    IF v_version_id IS NULL THEN
        UPDATE public.match_completed_events
        SET status = 'failed', error_message = 'Match not found'
        WHERE id = NEW.id;
        RETURN NEW;
    END IF;

    -- 2. Advance Winner
    FOR v_edge IN 
        SELECT target_match_id, target_slot 
        FROM public.brkt_advancements 
        WHERE version_id = v_version_id 
          AND source_match_id = NEW.match_id 
          AND type = 'winner'
    LOOP
        v_update_field := CASE WHEN v_edge.target_slot = 1 THEN 'team1_id' ELSE 'team2_id' END;
        
        UPDATE public.brkt_matches
        SET 
            -- Update the team
            team1_id = CASE WHEN v_edge.target_slot = 1 THEN NEW.winner_id ELSE team1_id END,
            team2_id = CASE WHEN v_edge.target_slot = 2 THEN NEW.winner_id ELSE team2_id END,
            -- Update overall version for optimistic locking
            version = version + 1
        WHERE id = v_edge.target_match_id;
    END LOOP;

    -- 3. Advance Loser
    FOR v_edge IN 
        SELECT target_match_id, target_slot 
        FROM public.brkt_advancements 
        WHERE version_id = v_version_id 
          AND source_match_id = NEW.match_id 
          AND type = 'loser'
    LOOP
        UPDATE public.brkt_matches
        SET 
            team1_id = CASE WHEN v_edge.target_slot = 1 THEN NEW.loser_id ELSE team1_id END,
            team2_id = CASE WHEN v_edge.target_slot = 2 THEN NEW.loser_id ELSE team2_id END,
            version = version + 1
        WHERE id = v_edge.target_match_id;
    END LOOP;

    -- 4. Mark as processed
    NEW.status := 'processed';
    NEW.processed_at := now();

    -- 5. Trigger cache rebuild notify (optional, but good practice)
    PERFORM pg_notify('bracket_update', json_build_object('version_id', v_version_id)::text);

    RETURN NEW;
EXCEPTION WHEN OTHERS THEN
    NEW.status := 'failed';
    NEW.error_message := SQLERRM;
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.process_session_refund(p_session_id uuid, p_venue_id uuid, p_amount numeric, p_method text, p_reason text, p_refunded_by uuid) RETURNS TABLE(refund_id uuid, wallet_txn_id uuid, total_refunded numeric)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
  v_session RECORD; v_refund_id UUID; v_wallet_txn_id UUID := NULL; v_total_refunded NUMERIC; v_wallet_id UUID; v_new_balance NUMERIC;
BEGIN
  SELECT id, venue_id, total_charged, user_id, COALESCE(refund_amount, 0) AS already_refunded INTO v_session
  FROM venue_sessions WHERE id = p_session_id AND venue_id = p_venue_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Session not found'; END IF;
  v_total_refunded := v_session.already_refunded + p_amount;
  IF v_total_refunded > v_session.total_charged THEN
    RAISE EXCEPTION 'Refund exceeds remaining refundable';
  END IF;
  IF p_method = 'wallet' THEN
    IF v_session.user_id IS NULL THEN RAISE EXCEPTION 'Cannot refund to wallet: no linked user'; END IF;
    INSERT INTO customer_wallets (user_id, venue_id, currency)
    SELECT v_session.user_id, p_venue_id, COALESCE((SELECT currency FROM venue_billing_config WHERE venue_id = p_venue_id LIMIT 1), 'GBP')
    ON CONFLICT (user_id, venue_id) DO NOTHING;
    SELECT id INTO v_wallet_id FROM customer_wallets WHERE user_id = v_session.user_id AND venue_id = p_venue_id;
    UPDATE customer_wallets SET balance = balance + p_amount WHERE id = v_wallet_id RETURNING balance INTO v_new_balance;
    INSERT INTO wallet_transactions (wallet_id, type, amount, balance_after, description, reference_id)
    VALUES (v_wallet_id, 'refund', p_amount, v_new_balance, 'Session refund: ' || p_reason, p_session_id::text)
    RETURNING id INTO v_wallet_txn_id;
  END IF;
  INSERT INTO session_refunds (venue_id, session_id, amount, method, reason, wallet_txn_id, refunded_by)
  VALUES (p_venue_id, p_session_id, p_amount, p_method, p_reason, v_wallet_txn_id, p_refunded_by) RETURNING id INTO v_refund_id;
  UPDATE venue_sessions SET refund_amount = v_total_refunded, refund_method = p_method,
    refund_reason = CASE WHEN refund_reason IS NULL THEN p_reason ELSE refund_reason || '; ' || p_reason END,
    refunded_at = NOW(), refunded_by = p_refunded_by WHERE id = p_session_id;
  RETURN QUERY SELECT v_refund_id, v_wallet_txn_id, v_total_refunded;
END;
$$;



CREATE FUNCTION public.refresh_daily_sponsor_stats() RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
  INSERT INTO public.daily_sponsor_stats (sponsor_id, stat_date, impressions, clicks)
  SELECT
    si.sponsor_id,
    si.created_at::date AS stat_date,
    COUNT(*) FILTER (WHERE si.event_type = 'impression') AS impressions,
    COUNT(*) FILTER (WHERE si.event_type = 'click') AS clicks
  FROM public.sponsor_impressions si
  WHERE si.created_at >= (now() - interval '2 days')
  GROUP BY si.sponsor_id, si.created_at::date
  ON CONFLICT (sponsor_id, stat_date)
  DO UPDATE SET
    impressions = EXCLUDED.impressions,
    clicks = EXCLUDED.clicks;
END;
$$;



CREATE FUNCTION public.refresh_daily_stats(p_venue_id uuid, p_date date) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
    v_total_sessions      INT;
    v_total_hours         NUMERIC(10,2);
    v_session_revenue     NUMERIC(10,2);
    v_pos_revenue         NUMERIC(10,2);
    v_package_revenue     NUMERIC(10,2);
    v_unique_members      INT;
    v_new_members         INT;
    v_walk_ins            INT;
    v_avg_session_minutes NUMERIC(8,2);
    v_peak_hour           INT;
    v_avg_utilization     NUMERIC(5,2);
    v_total_orders        INT;
    v_total_stations      INT;
BEGIN
    -- Verify caller is venue owner or active staff
    IF NOT EXISTS (
        SELECT 1 FROM venues WHERE id = p_venue_id AND owner_id = auth.uid()
    ) AND NOT EXISTS (
        SELECT 1 FROM venue_staff WHERE venue_id = p_venue_id AND user_id = auth.uid() AND status = 'active'
    ) THEN
        RAISE EXCEPTION 'Unauthorized: not owner or staff of this venue';
    END IF;

    -- ── Session stats ──────────────────────────────────────────
    SELECT
        COUNT(*),
        COALESCE(SUM(
            EXTRACT(EPOCH FROM (COALESCE(ended_at, NOW()) - started_at)) / 3600
        ), 0),
        COALESCE(SUM(total_charged), 0),
        COALESCE(AVG(
            EXTRACT(EPOCH FROM (COALESCE(ended_at, NOW()) - started_at)) / 60
        ), 0)
    INTO v_total_sessions, v_total_hours, v_session_revenue, v_avg_session_minutes
    FROM venue_sessions
    WHERE venue_id = p_venue_id
      AND started_at >= p_date
      AND started_at < p_date + INTERVAL '1 day';

    -- ── POS revenue ────────────────────────────────────────────
    SELECT COALESCE(SUM(total), 0), COUNT(*)
    INTO v_pos_revenue, v_total_orders
    FROM pos_orders
    WHERE venue_id = p_venue_id
      AND created_at >= p_date
      AND created_at < p_date + INTERVAL '1 day'
      AND status != 'cancelled';

    -- ── Package revenue (purchased that day) ───────────────────
    SELECT COALESCE(SUM(vp.price), 0)
    INTO v_package_revenue
    FROM member_packages mp
    JOIN venue_packages vp ON vp.id = mp.package_id
    WHERE mp.venue_id = p_venue_id
      AND mp.purchased_at >= p_date
      AND mp.purchased_at < p_date + INTERVAL '1 day';

    -- ── Unique members who had sessions ────────────────────────
    SELECT COUNT(DISTINCT member_id)
    INTO v_unique_members
    FROM venue_sessions
    WHERE venue_id = p_venue_id
      AND started_at >= p_date
      AND started_at < p_date + INTERVAL '1 day'
      AND member_id IS NOT NULL;

    -- ── New members created that day ───────────────────────────
    SELECT COUNT(*)
    INTO v_new_members
    FROM members
    WHERE venue_id = p_venue_id
      AND created_at >= p_date
      AND created_at < p_date + INTERVAL '1 day';

    -- ── Walk-ins (sessions without member) ─────────────────────
    SELECT COUNT(*)
    INTO v_walk_ins
    FROM venue_sessions
    WHERE venue_id = p_venue_id
      AND started_at >= p_date
      AND started_at < p_date + INTERVAL '1 day'
      AND member_id IS NULL;

    -- ── Peak hour ──────────────────────────────────────────────
    SELECT EXTRACT(HOUR FROM started_at)::INT
    INTO v_peak_hour
    FROM venue_sessions
    WHERE venue_id = p_venue_id
      AND started_at >= p_date
      AND started_at < p_date + INTERVAL '1 day'
    GROUP BY EXTRACT(HOUR FROM started_at)
    ORDER BY COUNT(*) DESC
    LIMIT 1;

    -- ── Utilization (% of station-hours used) ──────────────────
    SELECT COUNT(*) INTO v_total_stations
    FROM venue_stations
    WHERE venue_id = p_venue_id;

    IF v_total_stations > 0 THEN
        -- Max possible hours = stations × 24
        v_avg_utilization := LEAST(
            ROUND((v_total_hours / (v_total_stations * 24.0)) * 100, 2),
            100.0
        );
    ELSE
        v_avg_utilization := 0;
    END IF;

    -- ── Upsert the daily_stats row ─────────────────────────────
    INSERT INTO daily_stats (
        venue_id, date,
        total_sessions, total_hours, session_revenue,
        pos_revenue, package_revenue, total_revenue,
        unique_members, new_members, walk_ins,
        avg_session_minutes, peak_hour, avg_utilization,
        total_orders
    ) VALUES (
        p_venue_id, p_date,
        v_total_sessions, v_total_hours, v_session_revenue,
        v_pos_revenue, v_package_revenue,
        v_session_revenue + v_pos_revenue + v_package_revenue,
        v_unique_members, v_new_members, v_walk_ins,
        v_avg_session_minutes, v_peak_hour, v_avg_utilization,
        v_total_orders
    )
    ON CONFLICT (venue_id, date) DO UPDATE SET
        total_sessions      = EXCLUDED.total_sessions,
        total_hours         = EXCLUDED.total_hours,
        session_revenue     = EXCLUDED.session_revenue,
        pos_revenue         = EXCLUDED.pos_revenue,
        package_revenue     = EXCLUDED.package_revenue,
        total_revenue       = EXCLUDED.total_revenue,
        unique_members      = EXCLUDED.unique_members,
        new_members         = EXCLUDED.new_members,
        walk_ins            = EXCLUDED.walk_ins,
        avg_session_minutes = EXCLUDED.avg_session_minutes,
        peak_hour           = EXCLUDED.peak_hour,
        avg_utilization     = EXCLUDED.avg_utilization,
        total_orders        = EXCLUDED.total_orders,
        updated_at          = now();
END;
$$;



CREATE FUNCTION public.reject_sponsor_analytics_event_update() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'sponsor analytics events are immutable';
END;
$$;



CREATE FUNCTION public.reject_verification_request(request_id uuid, rejection_reason text, admin_notes text DEFAULT NULL::text) RETURNS json
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
    result JSON;
BEGIN
    -- Update the request status
    UPDATE public.verification_requests 
    SET 
        status = 'rejected',
        reviewed_by = auth.uid(),
        reviewed_at = NOW(),
        rejection_reason = rejection_reason,
        verification_notes = admin_notes
    WHERE id = request_id AND status = 'pending';
    
    IF NOT FOUND THEN
        RETURN json_build_object('success', false, 'message', 'Verification request not found or not pending');
    END IF;
    
    result := json_build_object(
        'success', true,
        'message', 'Verification request rejected'
    );
    
    RETURN result;
END;
$$;



CREATE FUNCTION public.remove_user_role(p_user_id uuid, p_role text) RETURNS boolean
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
  -- Don't allow removing the last role
  IF (SELECT COUNT(*) FROM public.user_roles WHERE user_id = p_user_id AND is_active = true) <= 1 THEN
    RAISE EXCEPTION 'Cannot remove the last active role from user';
  END IF;
  
  -- Deactivate the role
  UPDATE public.user_roles 
  SET is_active = false, updated_at = NOW()
  WHERE user_id = p_user_id AND role = p_role;
  
  -- If this was the primary role, set another role as primary
  IF (SELECT is_primary FROM public.user_roles WHERE user_id = p_user_id AND role = p_role) THEN
    UPDATE public.user_roles 
    SET is_primary = true, updated_at = NOW()
    WHERE user_id = p_user_id 
      AND is_active = true 
      AND role != p_role
    AND id = (
      SELECT id FROM public.user_roles 
      WHERE user_id = p_user_id 
        AND is_active = true 
        AND role != p_role 
      ORDER BY assigned_at ASC 
      LIMIT 1
    );
  END IF;
  
  RETURN true;
END;
$$;



CREATE FUNCTION public.reorder_tournament_stages(p_stage_ids uuid[]) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
    FOR i IN 1..array_length(p_stage_ids, 1) LOOP
        UPDATE tournament_stages
        SET stage_order = i,
            updated_at = NOW()
        WHERE id = p_stage_ids[i];
    END LOOP;
END;
$$;



CREATE FUNCTION public.reset_match_veto(p_match_id uuid) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
  v_veto_id uuid;
BEGIN
  SELECT id INTO v_veto_id
  FROM public.match_map_vetos
  WHERE match_id = p_match_id;
  
  IF NOT FOUND THEN
    RETURN;
  END IF;
  
  DELETE FROM public.match_map_veto_actions
  WHERE veto_id = v_veto_id;
  
  UPDATE public.match_map_vetos
  SET
    status = 'pending',
    current_team_id = NULL, -- CLEAR IT to force BO dialog or auto-init
    current_action = NULL, -- CLEAR IT
    current_action_number = 1,
    team1_banned_maps = '{}',
    team2_banned_maps = '{}',
    team1_picked_maps = '[]'::jsonb,
    team2_picked_maps = '[]'::jsonb,
    selected_map_id = null,
    best_of = NULL, -- CLEAR IT to force re-selection
    turn_started_at = NULL,
    started_at = NULL,
    completed_at = null,
    updated_at = now()
  WHERE id = v_veto_id;
END;
$$;



CREATE FUNCTION public.reset_tournament_bracket(p_tournament_id uuid) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
  v_veto_ids uuid[];
BEGIN
  -- 1. Delete match results
  DELETE FROM public.tournament_match_results
  WHERE tournament_id = p_tournament_id;

  -- 2. Reset Vetos (logic from reset_tournament_vetos)
  -- Get all veto IDs for this tournament
  SELECT array_agg(id) INTO v_veto_ids
  FROM public.match_map_vetos
  WHERE tournament_id = p_tournament_id;

  IF v_veto_ids IS NOT NULL THEN
    -- Delete all actions for these vetos
    DELETE FROM public.match_map_veto_actions
    WHERE veto_id = ANY(v_veto_ids);

    -- Reset veto state (NOTE: best_of is NOT reset - it will be auto-updated by the UI)
    UPDATE public.match_map_vetos
    SET
      status = 'pending',
      current_team_id = null,
      current_action = null,
      current_action_number = 0,
      team1_banned_maps = '{}',
      team2_banned_maps = '{}',
      team1_picked_maps = '[]',
      team2_picked_maps = '[]',
      selected_map_id = null,
      -- best_of is intentionally NOT reset here
      turn_started_at = null,
      started_at = null,
      completed_at = null,
      updated_at = now()
    WHERE id = ANY(v_veto_ids);
  END IF;

  -- 3. Reset Seeded Matches (Winners Bracket Round 1) - KEEP TEAMS
  -- We identify these by round=1 AND (bracket_side='winners' OR bracket_side IS NULL)
  UPDATE public.tournament_matches
  SET
    status = 'pending',
    team1_score = null,
    team2_score = null,
    winner_team_id = null,
    party_code = null,
    updated_at = now()
  WHERE tournament_id = p_tournament_id 
    AND round = 1 
    AND (bracket_side = 'winners' OR bracket_side IS NULL);

  -- 4. Reset Dependent Matches (Lower Bracket Round 1, and ALL other rounds) - CLEAR TEAMS
  -- This includes:
  -- - Round 1 of Losers Bracket
  -- - Round > 1 of Winners Bracket
  -- - Round > 1 of Losers Bracket
  -- - Finals, Reset, etc.
  UPDATE public.tournament_matches
  SET
    status = 'pending',
    team1_score = null,
    team2_score = null,
    winner_team_id = null,
    team1_id = null,
    team2_id = null,
    party_code = null,
    updated_at = now()
  WHERE tournament_id = p_tournament_id 
    AND NOT (round = 1 AND (bracket_side = 'winners' OR bracket_side IS NULL));

END;
$$;



CREATE FUNCTION public.reset_tournament_vetos(p_tournament_id uuid) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
  v_veto_ids uuid[];
BEGIN
  -- Get all veto IDs for this tournament
  SELECT array_agg(id) INTO v_veto_ids
  FROM public.valorant_match_map_vetos
  WHERE tournament_id = p_tournament_id;

  IF v_veto_ids IS NULL THEN
    RETURN;
  END IF;

  -- Delete all actions for these vetos
  DELETE FROM public.valorant_match_map_veto_actions
  WHERE veto_id = ANY(v_veto_ids);

  -- Reset veto state
  UPDATE public.valorant_match_map_vetos
  SET
    status = 'pending',
    current_team_id = null,
    current_action = null,
    current_action_number = 0,
    team1_banned_maps = '{}',
    team2_banned_maps = '{}',
    team1_picked_maps = '{}',
    team2_picked_maps = '{}',
    selected_map_id = null,
    best_of = null,
    selected_map_pool = null,
    turn_started_at = null,
    started_at = null,
    completed_at = null,
    updated_at = now()
  WHERE id = ANY(v_veto_ids);
END;
$$;



CREATE FUNCTION public.resolve_roster_capacity_limits(p_roster_id uuid, OUT max_players integer, OUT max_coaches integer, OUT allows_coaches boolean) RETURNS record
    LANGUAGE plpgsql STABLE SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
    v_game text;
    v_format text;
    v_team_size int;
    v_game_slug text;
    v_mode_max_roster int;
    v_mode_team_size int;
    v_mode_max_coaches int;
    v_mode_allows_coaches boolean;
BEGIN
    SELECT r.game, r.format, r.team_size
    INTO v_game, v_format, v_team_size
    FROM public.team_rosters r
    WHERE r.id = p_roster_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Roster not found';
    END IF;

    SELECT a.game_slug
    INTO v_game_slug
    FROM public.game_catalog_versions v
    JOIN public.game_catalog_game_aliases a ON a.version_id = v.id
    WHERE v.is_active = TRUE
      AND v.status = 'active'
      AND LOWER(a.alias) = LOWER(COALESCE(v_game, ''))
    LIMIT 1;

    IF v_game_slug IS NOT NULL THEN
        IF v_format IS NOT NULL AND BTRIM(v_format) <> '' THEN
            SELECT m.max_roster_size,
                   m.team_size,
                   COALESCE(m.max_coaches, 2),
                   COALESCE(m.allows_coaches, TRUE)
            INTO v_mode_max_roster, v_mode_team_size, v_mode_max_coaches, v_mode_allows_coaches
            FROM public.game_catalog_versions v
            JOIN public.game_catalog_game_modes m ON m.version_id = v.id
            WHERE v.is_active = TRUE
              AND v.status = 'active'
              AND m.game_slug = v_game_slug
              AND (
                  LOWER(m.mode_key) = LOWER(v_format)
                  OR EXISTS (
                      SELECT 1
                      FROM unnest(COALESCE(m.aliases, ARRAY[]::text[])) AS alias(value)
                      WHERE LOWER(alias.value) = LOWER(v_format)
                  )
              )
            ORDER BY m.mode_key ASC
            LIMIT 1;

            IF FOUND THEN
                max_players := COALESCE(v_mode_max_roster, v_mode_team_size);
                max_coaches := v_mode_max_coaches;
                allows_coaches := v_mode_allows_coaches;
                RETURN;
            END IF;
        END IF;

        IF v_mode_team_size IS NULL THEN
            SELECT m.max_roster_size,
                   m.team_size,
                   COALESCE(m.max_coaches, 2),
                   COALESCE(m.allows_coaches, TRUE)
            INTO v_mode_max_roster, v_mode_team_size, v_mode_max_coaches, v_mode_allows_coaches
            FROM public.game_catalog_versions v
            JOIN public.game_catalog_game_modes m ON m.version_id = v.id
            WHERE v.is_active = TRUE
              AND v.status = 'active'
              AND m.game_slug = v_game_slug
              AND m.team_size = v_team_size
            ORDER BY m.mode_key ASC
            LIMIT 1;
        END IF;

        IF v_mode_team_size IS NOT NULL THEN
            max_players := COALESCE(v_mode_max_roster, v_mode_team_size);
            max_coaches := v_mode_max_coaches;
            allows_coaches := v_mode_allows_coaches;
            RETURN;
        END IF;
    END IF;

    max_players := v_team_size;
    max_coaches := 2;
    allows_coaches := TRUE;
END;
$$;



CREATE FUNCTION public.rollup_daily_sponsor_stats() RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
  INSERT INTO public.daily_sponsor_stats (sponsor_id, stat_date, impressions, clicks, unique_impressions)
  SELECT
    si.sponsor_id,
    DATE(si.created_at) AS stat_date,
    COUNT(*) FILTER (WHERE si.event_type = 'impression') AS impressions,
    COUNT(*) FILTER (WHERE si.event_type = 'click') AS clicks,
    COUNT(DISTINCT si.visitor_id) FILTER (WHERE si.event_type = 'impression') AS unique_impressions
  FROM public.sponsor_impressions si
  WHERE si.sponsor_id IS NOT NULL
  GROUP BY si.sponsor_id, DATE(si.created_at)
  ON CONFLICT (sponsor_id, stat_date)
  DO UPDATE SET
    impressions     = EXCLUDED.impressions,
    clicks          = EXCLUDED.clicks,
    unique_impressions = EXCLUDED.unique_impressions;
END;
$$;



CREATE FUNCTION public.seed_stage_from_registrations(p_stage_id uuid) RETURNS integer
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
    v_tournament_id UUID;
    v_team_id UUID;
    v_count INTEGER := 0;
BEGIN
    SELECT tournament_id INTO v_tournament_id FROM tournament_stages WHERE id = p_stage_id;
    IF v_tournament_id IS NULL THEN RAISE EXCEPTION 'Stage not found'; END IF;

    FOR v_team_id IN (
        SELECT team_id FROM tournament_participants 
        WHERE tournament_id = v_tournament_id 
        AND status IN ('approved', 'checked_in') -- Allow both approved and checked_in
        AND team_id IS NOT NULL
    ) LOOP
        IF NOT EXISTS (SELECT 1 FROM stage_participants WHERE stage_id = p_stage_id AND team_id = v_team_id) THEN
            INSERT INTO stage_participants (stage_id, team_id) VALUES (p_stage_id, v_team_id);
            v_count := v_count + 1;
        END IF;
    END LOOP;
    RETURN v_count;
END;
$$;



CREATE FUNCTION public.set_stage1_initial_capacity() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
BEGIN
    -- When Stage 1 is created, set capacity from tournament's max_teams
    IF NEW.stage_order = 1 AND NEW.capacity IS NULL THEN
        SELECT max_teams INTO NEW.capacity
        FROM tournaments
        WHERE id = NEW.tournament_id;
    END IF;
    
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.suspend_user(p_user_id uuid, p_reason text, p_duration_days integer DEFAULT 7) RETURNS boolean
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
    v_suspension_until TIMESTAMP;
BEGIN
    v_suspension_until := NOW() + (p_duration_days || ' days')::INTERVAL;
    
    UPDATE profiles 
    SET 
        is_suspended = true,
        suspension_reason = p_reason,
        suspension_until = v_suspension_until
    WHERE id = p_user_id;
    
    RETURN FOUND;
END;
$$;



CREATE FUNCTION public.sync_stage1_capacity() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
BEGIN
    -- When max_teams is updated on tournaments, update Stage 1 capacity
    IF TG_OP = 'UPDATE' AND OLD.max_teams IS DISTINCT FROM NEW.max_teams THEN
        UPDATE tournament_stages
        SET capacity = NEW.max_teams
        WHERE tournament_id = NEW.id
          AND stage_order = 1;
    END IF;
    
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.sync_veto_bestof_from_stage() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
BEGIN
    -- Get best_of from stage and set it on the veto
    IF NEW.stage_id IS NOT NULL THEN
        SELECT best_of INTO NEW.best_of 
        FROM public.tournament_stages 
        WHERE id = NEW.stage_id;
    END IF;
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.touch_system_config_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
  NEW.updated_at = now();
  RETURN NEW;
END;
$$;



CREATE FUNCTION public.trigger_match_completed_webhook() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    AS $$
DECLARE
    v_url TEXT;
    v_secret TEXT;
    v_payload JSON;
    v_request_id BIGINT;
BEGIN
    SELECT value INTO v_url FROM decrypted_secrets WHERE name = 'WEBHOOK_URL_MATCH_COMPLETED';
    SELECT value INTO v_secret FROM decrypted_secrets WHERE name = 'WEBHOOK_SECRET_MATCH_COMPLETED';

    -- Build payload manually for net/http extension (assuming supabase-specific net package)
    v_payload := json_build_object(
        'type', 'INSERT',
        'table', TG_TABLE_NAME,
        'schema', TG_TABLE_SCHEMA,
        'record', row_to_json(NEW)
    );

    IF v_url IS NOT NULL THEN
        -- Fire and forget using pg_net extension deployed on Supabase
        SELECT net.http_post(
            url := v_url,
            headers := jsonb_build_object(
                'Content-Type', 'application/json',
                'Authorization', 'Bearer ' || v_secret
            ),
            body := v_payload::jsonb
        ) INTO v_request_id;
    END IF;

    RETURN NEW;
EXCEPTION
    WHEN OTHERS THEN
        -- Do not fail the transaction if webhook fails to fire
        RAISE WARNING 'Webhook invocation failed: %', SQLERRM;
        RETURN NEW;
END;
$$;



CREATE FUNCTION public.undo_match_advancement(p_match_id uuid) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    AS $$
BEGIN
    -- 1. Authorization Check (Enterprise Grade)
    IF NOT public.is_admin() AND NOT EXISTS (
        SELECT 1 FROM public.tournaments t
        JOIN public.brkt_versions v ON t.id = v.tournament_id
        JOIN public.brkt_matches m ON v.id = m.version_id
        WHERE m.id = p_match_id
        AND (t.organizer_id = auth.uid() OR t.organization_id IN (SELECT id FROM organizations WHERE owner_id = auth.uid()))
    ) THEN
        RAISE EXCEPTION 'Unauthorized to undo advancement for this match';
    END IF;

    -- 2. Atomic Reversion (Slot-Aware & Robust)
    -- This handles any target match that received a team from the source match.
    -- We null out the specific slots and reset the status to pending.
    UPDATE public.brkt_matches target
    SET 
        team1_id = CASE 
            WHEN EXISTS (
                SELECT 1 FROM public.brkt_advancements a 
                WHERE a.source_match_id = p_match_id 
                  AND a.target_match_id = target.id 
                  AND a.target_slot = 1
            ) THEN NULL ELSE team1_id END,
        team2_id = CASE 
            WHEN EXISTS (
                SELECT 1 FROM public.brkt_advancements a 
                WHERE a.source_match_id = p_match_id 
                  AND a.target_match_id = target.id 
                  AND a.target_slot = 2
            ) THEN NULL ELSE team2_id END,
        status = 'pending',
        winner_id = NULL,
        loser_id = NULL,
        version = version + 1,
        updated_at = now()
    WHERE id IN (
        SELECT target_match_id 
        FROM public.brkt_advancements 
        WHERE source_match_id = p_match_id
    );

    -- 3. Log Audit Event
    INSERT INTO public.brkt_match_events (match_id, type, payload)
    VALUES (p_match_id, 'match_reset', jsonb_build_object(
        'timestamp', now(),
        'undone_by', auth.uid()
    ));

END;
$$;



CREATE FUNCTION public.update_br_lobby_evidence_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    NEW.updated_at := now();
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.update_br_round_evidence_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    NEW.updated_at := now();
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.update_cs2_map_scores_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
BEGIN
  NEW.updated_at = NOW();
  RETURN NEW;
END;
$$;



CREATE FUNCTION public.update_customer_wallets_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.update_game_servers_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
  NEW.updated_at = now();
  RETURN NEW;
END;
$$;



CREATE FUNCTION public.update_loyalty_accounts_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.update_match_draft_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
BEGIN
  NEW.updated_at = NOW();
  RETURN NEW;
END;
$$;



CREATE FUNCTION public.update_organizations_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.update_pos_orders_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.update_report_schedules_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.update_system_settings_timestamp() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.update_tournament_registrations_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.update_updated_at_column() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.update_venue_announcements_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.update_venue_combos_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.update_venue_loyalty_config_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.update_venue_menu_items_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;



CREATE FUNCTION public.user_has_permission(p_user_id uuid, p_permission text) RETURNS boolean
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
  RETURN EXISTS(
    SELECT 1 
    FROM public.user_roles ur
    JOIN public.role_permissions rp ON ur.role = rp.role
    WHERE ur.user_id = p_user_id 
      AND ur.is_active = true
      AND rp.permission = p_permission
  );
END;
$$;



CREATE FUNCTION public.user_has_role(p_user_id uuid, p_role text) RETURNS boolean
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
  RETURN EXISTS(
    SELECT 1 FROM public.user_roles 
    WHERE user_id = p_user_id 
      AND role = p_role 
      AND is_active = true
  );
END;
$$;



CREATE FUNCTION public.validate_booking_code(p_code text, p_station_id text, p_venue_id uuid) RETURNS jsonb
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
  v_booking venue_bookings%ROWTYPE;
  v_now TIMESTAMPTZ := NOW();
  v_starts_at TIMESTAMPTZ;
  v_ends_at TIMESTAMPTZ;
BEGIN
  -- Only service role (hub) can call this RPC
  IF current_setting('request.jwt.claim.role', true) != 'service_role' THEN
    RAISE EXCEPTION 'Unauthorized: only service role can validate booking codes';
  END IF;

  IF p_code IS NULL OR TRIM(p_code) = '' THEN
    RETURN jsonb_build_object('valid', false, 'message', 'Booking code is required');
  END IF;

  SELECT * INTO v_booking
  FROM venue_bookings
  WHERE booking_code = UPPER(TRIM(p_code))
    AND venue_id = p_venue_id
    AND code_used_at IS NULL
    AND status = 'confirmed'
  FOR UPDATE SKIP LOCKED;

  IF NOT FOUND THEN
    RETURN jsonb_build_object('valid', false, 'message', 'Invalid or expired booking code');
  END IF;

  -- Build timestamps from date + time (compared as naive, same as NOW() in DB timezone)
  v_starts_at := v_booking.booking_date + v_booking.start_time;
  v_ends_at   := v_booking.booking_date + v_booking.end_time;

  -- Allow entry 15 minutes early
  IF v_now < v_starts_at - INTERVAL '15 minutes' THEN
    RETURN jsonb_build_object(
      'valid', false,
      'message', 'Booking not yet active. Starts at ' || v_booking.start_time::TEXT
    );
  END IF;

  IF v_now > v_ends_at THEN
    RETURN jsonb_build_object('valid', false, 'message', 'Booking has expired');
  END IF;

  -- Check station preference
  IF v_booking.station_preference IS NOT NULL
    AND v_booking.station_preference != p_station_id THEN
    RETURN jsonb_build_object(
      'valid', false,
      'message', 'This code is for station ' || v_booking.station_preference
    );
  END IF;

  -- Consume the code
  UPDATE venue_bookings
  SET code_used_at = v_now,
      station_id   = p_station_id,
      status       = 'completed'
  WHERE id = v_booking.id;

  RETURN jsonb_build_object(
    'valid',            true,
    'message',          'Booking confirmed',
    'booking_id',       v_booking.id,
    'user_id',          v_booking.user_id,
    'duration_minutes', GREATEST(EXTRACT(EPOCH FROM (v_ends_at - v_now)) / 60, 1),
    'display_name',     COALESCE(v_booking.contact_email, 'Customer'),
    'session_type',     'web_booking'
  );
END;
$$;



CREATE FUNCTION public.validate_roster_name_match() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
begin
  if new.roster_id is not null and new.roster_name is not null then
    if not exists (
      select 1 from public.team_rosters 
      where id = new.roster_id 
        and name = new.roster_name
    ) then
      raise exception 'roster_name does not match the roster_id';
    end if;
  end if;
  return new;
end;
$$;



CREATE FUNCTION public.wallet_credit(p_wallet uuid, p_amount bigint, p_ref_type text, p_ref_id uuid, p_memo text) RETURNS boolean
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
BEGIN
  IF p_amount <= 0 THEN RAISE EXCEPTION 'amount must be > 0'; END IF;
  UPDATE wallets SET balance_cents = balance_cents + p_amount WHERE id=p_wallet;
  INSERT INTO ledger_entries(wallet_id, amount_cents, entry_type, reference_type, reference_id, memo)
  VALUES (p_wallet, p_amount, 'credit', p_ref_type, p_ref_id, p_memo);
  RETURN TRUE;
END; $$;



CREATE FUNCTION public.wallet_debit(p_wallet uuid, p_amount bigint, p_ref_type text, p_ref_id uuid, p_memo text) RETURNS boolean
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE bal BIGINT; BEGIN
  IF p_amount <= 0 THEN RAISE EXCEPTION 'amount must be > 0'; END IF;
  SELECT balance_cents INTO bal FROM wallets WHERE id=p_wallet;
  IF bal IS NULL THEN RAISE EXCEPTION 'wallet not found'; END IF;
  IF bal < p_amount THEN RAISE EXCEPTION 'insufficient funds'; END IF;
  UPDATE wallets SET balance_cents = balance_cents - p_amount WHERE id=p_wallet;
  INSERT INTO ledger_entries(wallet_id, amount_cents, entry_type, reference_type, reference_id, memo)
  VALUES (p_wallet, p_amount, 'debit', p_ref_type, p_ref_id, p_memo);
  RETURN TRUE;
END; $$;



CREATE FUNCTION public.wallet_deduct(p_wallet_id uuid, p_venue_id uuid, p_amount numeric, p_description text DEFAULT 'Session payment'::text, p_reference_id text DEFAULT NULL::text, p_created_by uuid DEFAULT NULL::uuid) RETURNS TABLE(success boolean, new_balance numeric, error_message text)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
    v_current_balance NUMERIC;
    v_new_balance NUMERIC;
BEGIN
    -- Lock the row and check balance atomically — now also verifies venue_id
    SELECT balance INTO v_current_balance
    FROM customer_wallets
    WHERE id = p_wallet_id AND venue_id = p_venue_id AND is_active = true
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT false, 0::NUMERIC, 'Wallet not found, inactive, or venue mismatch'::TEXT;
        RETURN;
    END IF;

    IF v_current_balance < p_amount THEN
        RETURN QUERY SELECT false, v_current_balance, 'Insufficient balance'::TEXT;
        RETURN;
    END IF;

    -- Atomic deduction
    v_new_balance := v_current_balance - p_amount;
    UPDATE customer_wallets
    SET balance = v_new_balance, updated_at = now()
    WHERE id = p_wallet_id;

    -- Record transaction
    INSERT INTO wallet_transactions (wallet_id, venue_id, type, amount, balance_after, description, reference_id, created_by)
    VALUES (p_wallet_id, p_venue_id, 'deduct', p_amount, v_new_balance, p_description, p_reference_id, p_created_by);

    RETURN QUERY SELECT true, v_new_balance, NULL::TEXT;
END;
$$;



CREATE FUNCTION public.wallet_topup(p_venue_id uuid, p_user_id uuid, p_amount numeric, p_currency text DEFAULT 'USD'::text, p_description text DEFAULT 'Top-up'::text, p_created_by uuid DEFAULT NULL::uuid) RETURNS TABLE(wallet_id uuid, new_balance numeric)
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'public'
    AS $$
DECLARE
    v_wallet_id UUID;
    v_new_balance NUMERIC;
BEGIN
    -- Find or create wallet with row lock
    INSERT INTO customer_wallets (user_id, venue_id, balance, currency)
    VALUES (p_user_id, p_venue_id, 0, p_currency)
    ON CONFLICT (user_id, venue_id) DO UPDATE SET updated_at = now()
    RETURNING id INTO v_wallet_id;

    -- Atomic balance update
    UPDATE customer_wallets
    SET balance = balance + p_amount, updated_at = now()
    WHERE id = v_wallet_id
    RETURNING balance INTO v_new_balance;

    -- Record transaction
    INSERT INTO wallet_transactions (wallet_id, venue_id, type, amount, balance_after, description, created_by)
    VALUES (v_wallet_id, p_venue_id, 'topup', p_amount, v_new_balance, p_description, p_created_by);

    RETURN QUERY SELECT v_wallet_id, v_new_balance;
END;
$$;



CREATE TABLE public.account_security_state (
    user_id uuid NOT NULL,
    sessions_valid_after bigint DEFAULT 0 NOT NULL,
    revocation_version bigint DEFAULT 0 NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT account_security_state_revocation_version_check CHECK ((revocation_version >= 0)),
    CONSTRAINT account_security_state_sessions_valid_after_check CHECK ((sessions_valid_after >= 0))
);



CREATE TABLE public.activity_log (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    actor_id uuid,
    actor_name text,
    action text NOT NULL,
    target_type text,
    target_id text,
    details jsonb DEFAULT '{}'::jsonb,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.admin_alerts (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    type text NOT NULL,
    severity text DEFAULT 'info'::text NOT NULL,
    title text NOT NULL,
    message text,
    data jsonb DEFAULT '{}'::jsonb,
    status text DEFAULT 'active'::text NOT NULL,
    acknowledged_by uuid,
    acknowledged_at timestamp with time zone,
    resolved_by uuid,
    resolved_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.admin_dashboard_preferences (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    layout jsonb DEFAULT '[]'::jsonb NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.admin_game_assignments (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    admin_id uuid NOT NULL,
    game_id text NOT NULL,
    scope text DEFAULT 'tournament_ops'::text NOT NULL,
    assigned_by uuid,
    assigned_at timestamp with time zone DEFAULT now() NOT NULL,
    expires_at timestamp with time zone
);



CREATE TABLE public.admin_impersonation_sessions (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    jti text NOT NULL,
    admin_id uuid NOT NULL,
    target_user_id uuid NOT NULL,
    reason text NOT NULL,
    scopes text[] DEFAULT ARRAY['support:read'::text] NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    revoked_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    last_seen_at timestamp with time zone,
    CONSTRAINT admin_impersonation_sessions_check CHECK ((expires_at <= (created_at + '00:15:00'::interval)))
);



CREATE TABLE public.admin_ip_allowlist (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    ip_address text NOT NULL,
    label text DEFAULT ''::text NOT NULL,
    created_by uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    expires_at timestamp with time zone,
    is_active boolean DEFAULT true NOT NULL
);



CREATE TABLE public.admin_permissions (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    name text NOT NULL,
    description text,
    resource text NOT NULL,
    action text NOT NULL,
    created_at timestamp with time zone DEFAULT now(),
    label text,
    category text,
    risk_level text DEFAULT 'normal'::text NOT NULL,
    sort_order integer DEFAULT 0 NOT NULL,
    is_system boolean DEFAULT false NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT admin_permissions_risk_level_check CHECK ((risk_level = ANY (ARRAY['normal'::text, 'sensitive'::text, 'destructive'::text, 'god_mode'::text])))
);



CREATE TABLE public.admin_role_permissions (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    role_id uuid NOT NULL,
    permission_id uuid NOT NULL,
    created_at timestamp with time zone DEFAULT now()
);



CREATE TABLE public.admin_roles (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    name text NOT NULL,
    description text,
    created_at timestamp with time zone DEFAULT now(),
    key text
);



CREATE TABLE public.admin_session_audit (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    session_id text,
    action text NOT NULL,
    ip_address inet,
    user_agent text,
    metadata jsonb DEFAULT '{}'::jsonb,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT admin_session_audit_action_check CHECK ((action = ANY (ARRAY['login'::text, 'logout'::text, 'activity'::text, 'refresh'::text])))
);



CREATE TABLE public.admin_user_roles (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    user_id uuid NOT NULL,
    role_id uuid NOT NULL,
    assigned_by uuid,
    assigned_at timestamp with time zone DEFAULT now()
);



CREATE TABLE public.anomaly_events (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    rule_id uuid NOT NULL,
    metric text NOT NULL,
    count_observed integer NOT NULL,
    threshold_count integer NOT NULL,
    window_minutes integer NOT NULL,
    detected_at timestamp with time zone DEFAULT now() NOT NULL,
    resolved_at timestamp with time zone,
    resolved_by uuid,
    is_resolved boolean DEFAULT false NOT NULL,
    details jsonb DEFAULT '{}'::jsonb NOT NULL
);



CREATE TABLE public.anomaly_rules (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    name text NOT NULL,
    description text DEFAULT ''::text NOT NULL,
    metric text NOT NULL,
    threshold_count integer NOT NULL,
    window_minutes integer NOT NULL,
    severity text DEFAULT 'medium'::text NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    last_triggered_at timestamp with time zone,
    cooldown_minutes integer DEFAULT 60 NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT anomaly_rules_severity_check CHECK ((severity = ANY (ARRAY['low'::text, 'medium'::text, 'high'::text, 'critical'::text])))
);



CREATE TABLE public.audit_logs (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    admin_id uuid,
    admin_name text DEFAULT ''::text NOT NULL,
    action_type text NOT NULL,
    target_type text NOT NULL,
    target_id text DEFAULT ''::text NOT NULL,
    target_name text DEFAULT ''::text NOT NULL,
    details jsonb DEFAULT '{}'::jsonb,
    ip_address text,
    user_agent text,
    severity text DEFAULT 'low'::text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.balance_transactions (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    amount numeric(10,2) NOT NULL,
    description text,
    venue_id uuid,
    session_id uuid,
    created_at timestamp with time zone DEFAULT now()
);



CREATE TABLE public.booking_rules (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    min_duration_minutes integer DEFAULT 30 NOT NULL,
    max_duration_minutes integer DEFAULT 480 NOT NULL,
    advance_booking_days integer DEFAULT 14 NOT NULL,
    cancellation_minutes integer DEFAULT 60 NOT NULL,
    no_show_cancel_minutes integer DEFAULT 15 NOT NULL,
    buffer_minutes integer DEFAULT 5 NOT NULL,
    max_stations_per_booking integer DEFAULT 1 NOT NULL,
    allow_walk_ins boolean DEFAULT true NOT NULL,
    require_payment_upfront boolean DEFAULT false NOT NULL,
    peak_hours jsonb DEFAULT '[]'::jsonb,
    off_peak_discount_percent numeric(5,2) DEFAULT 0,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.br_game_data (
    tournament_id text NOT NULL,
    games jsonb DEFAULT '{}'::jsonb NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_by uuid
);



CREATE TABLE public.br_games (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    lobby_id uuid NOT NULL,
    game_number integer NOT NULL,
    map text,
    status text DEFAULT 'pending'::text NOT NULL,
    scheduled_at timestamp with time zone,
    started_at timestamp with time zone,
    completed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    queue_timer_minutes integer,
    queue_started_at timestamp with time zone,
    CONSTRAINT br_games_queue_timer_minutes_check CHECK (((queue_timer_minutes IS NULL) OR ((queue_timer_minutes >= 0) AND (queue_timer_minutes <= 180)))),
    CONSTRAINT br_games_status_check CHECK ((status = ANY (ARRAY['pending'::text, 'active'::text, 'completed'::text])))
);



CREATE TABLE public.br_group_teams (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    group_id uuid NOT NULL,
    team_id uuid,
    seed_order integer DEFAULT 0 NOT NULL,
    assigned_at timestamp with time zone DEFAULT now() NOT NULL,
    participant_id uuid,
    CONSTRAINT br_group_teams_exactly_one_entity CHECK (((team_id IS NULL) <> (participant_id IS NULL)))
);



CREATE TABLE public.br_groups (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    stage_id uuid NOT NULL,
    name text DEFAULT 'Group A'::text NOT NULL,
    group_order integer DEFAULT 0 NOT NULL,
    lobby_size integer DEFAULT 20 NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.br_lobbies (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    stage_id uuid NOT NULL,
    wave_number integer NOT NULL,
    lobby_index integer DEFAULT 0 NOT NULL,
    lobby_code text,
    map text,
    status text DEFAULT 'pending'::text NOT NULL,
    scheduled_at timestamp with time zone,
    started_at timestamp with time zone,
    completed_at timestamp with time zone,
    queue_timer_minutes integer,
    queue_started_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT br_lobbies_queue_timer_minutes_check CHECK (((queue_timer_minutes IS NULL) OR ((queue_timer_minutes >= 0) AND (queue_timer_minutes <= 180)))),
    CONSTRAINT br_lobbies_status_check CHECK ((status = ANY (ARRAY['pending'::text, 'active'::text, 'completed'::text])))
);



CREATE TABLE public.br_lobby_evidence (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    team_id uuid,
    participant_id uuid,
    image_url text NOT NULL,
    submitted_by uuid NOT NULL,
    submitted_at timestamp with time zone DEFAULT now() NOT NULL,
    placement integer,
    kills integer,
    reviewed boolean DEFAULT false NOT NULL,
    reviewed_at timestamp with time zone,
    reviewed_by uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    lobby_id uuid,
    game_id uuid NOT NULL,
    CONSTRAINT br_round_evidence_exactly_one_entity CHECK (((team_id IS NULL) <> (participant_id IS NULL))),
    CONSTRAINT br_round_evidence_kills_check CHECK (((kills IS NULL) OR (kills >= 0))),
    CONSTRAINT br_round_evidence_placement_check CHECK (((placement IS NULL) OR (placement >= 1)))
);



CREATE TABLE public.br_lobby_groups (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    lobby_id uuid NOT NULL,
    group_id uuid NOT NULL
);



CREATE TABLE public.br_lobby_readiness (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    lobby_id uuid NOT NULL,
    team_id uuid,
    participant_id uuid,
    user_id uuid NOT NULL,
    game_id uuid,
    checked_in_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT br_lobby_readiness_entity_chk CHECK ((((team_id IS NOT NULL) AND (participant_id IS NULL)) OR ((team_id IS NULL) AND (participant_id IS NOT NULL))))
);



CREATE TABLE public.br_lobby_results (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    team_id uuid,
    placement integer NOT NULL,
    kills integer DEFAULT 0 NOT NULL,
    placement_points integer DEFAULT 0 NOT NULL,
    kill_points integer DEFAULT 0 NOT NULL,
    total_points integer GENERATED ALWAYS AS ((placement_points + kill_points)) STORED NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    participant_id uuid,
    lobby_id uuid,
    game_id uuid NOT NULL,
    CONSTRAINT br_round_results_exactly_one_entity CHECK (((team_id IS NULL) <> (participant_id IS NULL))),
    CONSTRAINT br_round_results_kills_check CHECK ((kills >= 0)),
    CONSTRAINT br_round_results_placement_check CHECK ((placement >= 1))
);



CREATE TABLE public.brkt_advancements (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    version_id uuid NOT NULL,
    source_match_id uuid NOT NULL,
    target_match_id uuid NOT NULL,
    type text NOT NULL,
    target_slot integer NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT brkt_advancements_target_slot_check CHECK ((target_slot = ANY (ARRAY[1, 2]))),
    CONSTRAINT brkt_advancements_type_check CHECK ((type = ANY (ARRAY['winner'::text, 'loser'::text])))
);



CREATE TABLE public.brkt_layout (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    version_id uuid NOT NULL,
    match_id uuid NOT NULL,
    x integer NOT NULL,
    y integer NOT NULL
);



CREATE TABLE public.brkt_match_events (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    match_id uuid NOT NULL,
    type text NOT NULL,
    payload jsonb DEFAULT '{}'::jsonb NOT NULL,
    created_by uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT brkt_match_events_type_check CHECK ((type = ANY (ARRAY['participant_ready'::text, 'score_reported'::text, 'dispute_opened'::text, 'match_finalized'::text, 'match_reset'::text, 'advancement_completed'::text, 'walkover_awarded'::text, 'manual_adjustment'::text])))
);



CREATE TABLE public.brkt_match_games (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    match_id uuid NOT NULL,
    game_number integer NOT NULL,
    map_id uuid,
    riot_match_id text,
    status text DEFAULT 'pending'::text,
    winner_id uuid,
    loser_id uuid,
    team1_score integer DEFAULT 0,
    team2_score integer DEFAULT 0,
    match_details jsonb,
    started_at timestamp with time zone,
    completed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    reported_by_team_id uuid,
    verification_status text DEFAULT 'pending'::text,
    dispute_reason text,
    proposal_data jsonb,
    mvp_id uuid,
    map_name text,
    CONSTRAINT brkt_match_games_status_check CHECK ((status = ANY (ARRAY['pending'::text, 'in_progress'::text, 'completed'::text]))),
    CONSTRAINT brkt_match_games_verification_status_check CHECK ((verification_status = ANY (ARRAY['pending'::text, 'proposed'::text, 'verified'::text, 'disputed'::text, 'rejected'::text])))
);

ALTER TABLE ONLY public.brkt_match_games REPLICA IDENTITY FULL;



CREATE TABLE public.brkt_matches (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    version_id uuid NOT NULL,
    round_index integer NOT NULL,
    match_number integer NOT NULL,
    bracket_type text NOT NULL,
    team1_id uuid,
    team2_id uuid,
    status text DEFAULT 'pending'::text NOT NULL,
    winner_id uuid,
    loser_id uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    party_code text,
    team1_score integer DEFAULT 0,
    team2_score integer DEFAULT 0,
    scheduled_time timestamp with time zone,
    best_of integer DEFAULT 3,
    updated_at timestamp with time zone DEFAULT now(),
    group_id text,
    round_number integer,
    result_notes text,
    check_in_reminder_sent boolean DEFAULT false,
    automated_report_status text DEFAULT 'idle'::text,
    version integer DEFAULT 1 NOT NULL,
    walkover_job_id text,
    team1_seed integer,
    team2_seed integer,
    CONSTRAINT brkt_matches_automated_report_status_check CHECK ((automated_report_status = ANY (ARRAY['idle'::text, 'processing'::text, 'verified'::text, 'failed'::text, 'partial'::text]))),
    CONSTRAINT brkt_matches_bracket_type_check CHECK ((bracket_type = ANY (ARRAY['winners'::text, 'losers'::text, 'final'::text, 'group'::text, 'swiss_round'::text, 'battle_royale_round'::text]))),
    CONSTRAINT brkt_matches_status_check CHECK ((status = ANY (ARRAY['pending'::text, 'scheduled'::text, 'in_progress'::text, 'completed'::text, 'disputed'::text])))
);

ALTER TABLE ONLY public.brkt_matches REPLICA IDENTITY FULL;



CREATE TABLE public.brkt_versions (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    tournament_id uuid NOT NULL,
    version_number integer NOT NULL,
    status text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    activated_at timestamp with time zone,
    stage_id uuid,
    cached_ui_state jsonb,
    CONSTRAINT brkt_versions_status_check CHECK ((status = ANY (ARRAY['draft'::text, 'active'::text, 'archived'::text])))
);



CREATE TABLE public.broadcast_deliveries (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    broadcast_id uuid NOT NULL,
    user_id uuid NOT NULL,
    channel text DEFAULT 'in_app'::text NOT NULL,
    status text DEFAULT 'pending'::text NOT NULL,
    delivered_at timestamp with time zone,
    read_at timestamp with time zone,
    error_message text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT broadcast_deliveries_status_check CHECK ((status = ANY (ARRAY['pending'::text, 'delivered'::text, 'read'::text, 'failed'::text])))
);



CREATE TABLE public.broadcast_licenses (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    license_key text NOT NULL,
    license_type text NOT NULL,
    status text DEFAULT 'active'::text NOT NULL,
    plan text DEFAULT 'standard'::text NOT NULL,
    activated_at timestamp with time zone DEFAULT now() NOT NULL,
    expires_at timestamp with time zone,
    last_validated_at timestamp with time zone DEFAULT now(),
    device_fingerprints jsonb DEFAULT '[]'::jsonb,
    max_devices integer DEFAULT 2 NOT NULL,
    metadata jsonb DEFAULT '{}'::jsonb,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT broadcast_licenses_license_type_check CHECK ((license_type = ANY (ARRAY['subscription'::text, 'one_time'::text]))),
    CONSTRAINT broadcast_licenses_plan_check CHECK ((plan = ANY (ARRAY['standard'::text, 'pro'::text, 'enterprise'::text]))),
    CONSTRAINT broadcast_licenses_status_check CHECK ((status = ANY (ARRAY['active'::text, 'expired'::text, 'revoked'::text, 'suspended'::text])))
);



CREATE TABLE public.broadcast_overlay_layouts (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    name text NOT NULL,
    game text DEFAULT 'valorant'::text NOT NULL,
    resolution_width integer DEFAULT 1920 NOT NULL,
    resolution_height integer DEFAULT 1080 NOT NULL,
    widgets jsonb DEFAULT '[]'::jsonb NOT NULL,
    is_template boolean DEFAULT false NOT NULL,
    is_public boolean DEFAULT false NOT NULL,
    thumbnail_url text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.broadcast_templates (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    name text NOT NULL,
    title text NOT NULL,
    content text NOT NULL,
    content_html text,
    broadcast_type text DEFAULT 'announcement'::text NOT NULL,
    created_by uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.broadcast_themes (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    owner_id uuid,
    name text NOT NULL,
    primary_color text DEFAULT '#E11D48'::text,
    secondary_color text DEFAULT '#0F0F0F'::text,
    accent_color text DEFAULT '#FFFFFF'::text,
    font_family text DEFAULT 'Inter'::text,
    logo_url text,
    background_url text,
    custom_css text,
    is_default boolean DEFAULT false,
    created_at timestamp with time zone DEFAULT now()
);



CREATE TABLE public.broadcasts (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    title text NOT NULL,
    content text NOT NULL,
    content_html text,
    broadcast_type text DEFAULT 'announcement'::text NOT NULL,
    priority text DEFAULT 'normal'::text NOT NULL,
    target_type text DEFAULT 'all'::text NOT NULL,
    target_segment jsonb,
    target_user_ids uuid[],
    channels text[] DEFAULT '{in_app}'::text[] NOT NULL,
    status text DEFAULT 'draft'::text NOT NULL,
    scheduled_at timestamp with time zone,
    sent_at timestamp with time zone,
    total_recipients integer DEFAULT 0 NOT NULL,
    delivered_count integer DEFAULT 0 NOT NULL,
    read_count integer DEFAULT 0 NOT NULL,
    created_by uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT broadcasts_priority_check CHECK ((priority = ANY (ARRAY['low'::text, 'normal'::text, 'high'::text, 'urgent'::text]))),
    CONSTRAINT broadcasts_status_check CHECK ((status = ANY (ARRAY['draft'::text, 'scheduled'::text, 'sending'::text, 'sent'::text, 'cancelled'::text, 'failed'::text]))),
    CONSTRAINT broadcasts_target_type_check CHECK ((target_type = ANY (ARRAY['all'::text, 'segment'::text, 'users'::text])))
);



CREATE TABLE public.consent_records (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    consent_type text NOT NULL,
    granted boolean NOT NULL,
    ip_address text,
    user_agent text,
    recorded_at timestamp with time zone DEFAULT now() NOT NULL,
    version text DEFAULT '1.0'::text NOT NULL
);



CREATE TABLE public.customer_wallets (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    user_id uuid NOT NULL,
    venue_id uuid NOT NULL,
    balance numeric(12,2) DEFAULT 0.00 NOT NULL,
    currency text DEFAULT 'USD'::text NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.daily_sponsor_stats (
    sponsor_id uuid NOT NULL,
    stat_date date NOT NULL,
    impressions bigint DEFAULT 0 NOT NULL,
    clicks bigint DEFAULT 0 NOT NULL,
    unique_impressions bigint DEFAULT 0 NOT NULL
);



CREATE TABLE public.daily_stats (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    date date NOT NULL,
    total_sessions integer DEFAULT 0 NOT NULL,
    total_hours numeric(10,2) DEFAULT 0 NOT NULL,
    session_revenue numeric(10,2) DEFAULT 0 NOT NULL,
    pos_revenue numeric(10,2) DEFAULT 0 NOT NULL,
    package_revenue numeric(10,2) DEFAULT 0 NOT NULL,
    total_revenue numeric(10,2) DEFAULT 0 NOT NULL,
    unique_members integer DEFAULT 0 NOT NULL,
    new_members integer DEFAULT 0 NOT NULL,
    walk_ins integer DEFAULT 0 NOT NULL,
    avg_session_minutes numeric(8,2) DEFAULT 0 NOT NULL,
    peak_hour integer,
    avg_utilization numeric(5,2) DEFAULT 0 NOT NULL,
    total_orders integer DEFAULT 0 NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.dispute_comments (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    dispute_id uuid NOT NULL,
    user_id uuid NOT NULL,
    comment text NOT NULL,
    is_internal boolean DEFAULT false,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    attachment_url text
);



CREATE TABLE public.dispute_read_receipts (
    dispute_id uuid NOT NULL,
    user_id uuid NOT NULL,
    last_read_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE SEQUENCE public.dispute_reference_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;



CREATE TABLE public.disputes (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    tournament_id uuid,
    match_id uuid,
    reporter_id uuid,
    status text DEFAULT 'open'::text NOT NULL,
    priority text DEFAULT 'normal'::text,
    category text,
    title text,
    description text,
    resolution text,
    resolved_by uuid,
    resolved_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT disputes_priority_check CHECK ((priority = ANY (ARRAY['low'::text, 'normal'::text, 'high'::text, 'critical'::text]))),
    CONSTRAINT disputes_status_check CHECK ((status = ANY (ARRAY['open'::text, 'in_progress'::text, 'resolved'::text, 'closed'::text])))
);



CREATE TABLE public.feature_flag_overrides (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    flag_id uuid NOT NULL,
    user_id uuid NOT NULL,
    value jsonb NOT NULL,
    reason text,
    created_by uuid,
    expires_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.feature_flag_rules (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    flag_id uuid NOT NULL,
    priority integer DEFAULT 0 NOT NULL,
    conditions jsonb DEFAULT '{}'::jsonb NOT NULL,
    value jsonb NOT NULL,
    percentage integer,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT feature_flag_rules_percentage_check CHECK (((percentage IS NULL) OR ((percentage >= 0) AND (percentage <= 100))))
);



CREATE TABLE public.feature_flags (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    key text NOT NULL,
    name text NOT NULL,
    description text,
    flag_type text DEFAULT 'boolean'::text NOT NULL,
    default_value jsonb DEFAULT '{"enabled": false}'::jsonb NOT NULL,
    is_enabled boolean DEFAULT true NOT NULL,
    created_by uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.game_catalog_game_aliases (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    version_id uuid NOT NULL,
    game_slug text NOT NULL,
    alias text NOT NULL,
    alias_type text DEFAULT 'legacy'::text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT chk_game_catalog_aliases_type CHECK ((alias_type = ANY (ARRAY['slug'::text, 'name'::text, 'legacy'::text])))
);



CREATE TABLE public.game_catalog_game_modes (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    version_id uuid NOT NULL,
    game_slug text NOT NULL,
    mode_key text NOT NULL,
    name text NOT NULL,
    team_size integer NOT NULL,
    participant_mode text NOT NULL,
    allows_substitutes boolean DEFAULT true NOT NULL,
    max_roster_size integer,
    aliases text[] DEFAULT ARRAY[]::text[] NOT NULL,
    raw jsonb DEFAULT '{}'::jsonb NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    display_group text,
    variant_label text,
    map_pool_filter text,
    features_override jsonb,
    max_substitutes integer,
    allows_coaches boolean DEFAULT true NOT NULL,
    max_coaches integer DEFAULT 2 NOT NULL,
    CONSTRAINT chk_game_catalog_modes_map_pool_filter CHECK (((map_pool_filter IS NULL) OR (map_pool_filter = ANY (ARRAY['standard'::text, 'skirmish'::text])))),
    CONSTRAINT chk_game_catalog_modes_participant_mode CHECK ((participant_mode = ANY (ARRAY['team'::text, 'solo'::text]))),
    CONSTRAINT chk_game_catalog_modes_roster_size CHECK (((max_roster_size IS NULL) OR (max_roster_size >= team_size))),
    CONSTRAINT chk_game_catalog_modes_team_size CHECK ((team_size > 0))
);



CREATE TABLE public.game_catalog_games (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    version_id uuid NOT NULL,
    slug text NOT NULL,
    name text NOT NULL,
    category text,
    game_type text NOT NULL,
    default_mode_key text NOT NULL,
    features jsonb DEFAULT '{}'::jsonb NOT NULL,
    br_config jsonb,
    raw jsonb DEFAULT '{}'::jsonb NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    logo_url text,
    icon_url text,
    cover_url text,
    sort_order integer DEFAULT 0 NOT NULL,
    banner_url text,
    CONSTRAINT chk_game_catalog_games_banner_url_https CHECK (((banner_url IS NULL) OR (banner_url ~ '^https://'::text))),
    CONSTRAINT chk_game_catalog_games_game_type CHECK ((game_type = ANY (ARRAY['bracket'::text, 'battle_royale'::text])))
);



CREATE TABLE public.game_catalog_tournament_structures (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    version_id uuid NOT NULL,
    game_slug text NOT NULL,
    structure_key text NOT NULL,
    name text NOT NULL,
    is_default boolean DEFAULT false NOT NULL,
    raw jsonb DEFAULT '{}'::jsonb NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT chk_game_catalog_structures_key CHECK ((structure_key = ANY (ARRAY['single_elimination'::text, 'double_elimination'::text, 'swiss'::text, 'round_robin'::text, 'battle_royale'::text])))
);



CREATE TABLE public.game_catalog_versions (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    catalog_version text NOT NULL,
    schema_version integer NOT NULL,
    content_hash text NOT NULL,
    status text DEFAULT 'pending'::text NOT NULL,
    is_active boolean DEFAULT false NOT NULL,
    error_message text,
    imported_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    source text DEFAULT 'packaged'::text NOT NULL,
    created_by uuid,
    published_by uuid,
    published_at timestamp with time zone,
    notes text,
    CONSTRAINT chk_game_catalog_versions_schema_version CHECK ((schema_version > 0)),
    CONSTRAINT chk_game_catalog_versions_source CHECK ((source = ANY (ARRAY['packaged'::text, 'admin'::text]))),
    CONSTRAINT chk_game_catalog_versions_status CHECK ((status = ANY (ARRAY['pending'::text, 'active'::text, 'failed'::text, 'draft'::text])))
);



CREATE TABLE public.game_maps (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    game text NOT NULL,
    map_name text NOT NULL,
    map_image_url text,
    is_active boolean DEFAULT true,
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now()
);



CREATE TABLE public.game_servers (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    match_id uuid NOT NULL,
    tournament_id uuid,
    provider text DEFAULT 'dathost'::text NOT NULL,
    external_id text NOT NULL,
    region text NOT NULL,
    ip text,
    raw_ip text,
    port integer,
    gotv_port integer,
    rcon_password text,
    map text,
    status text DEFAULT 'provisioning'::text NOT NULL,
    cost_per_hour numeric(8,4),
    server_name text,
    error_message text,
    started_at timestamp with time zone,
    stopped_at timestamp with time zone,
    deleted_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    matchzy_secret text,
    auto_managed boolean DEFAULT false NOT NULL,
    current_map_number integer DEFAULT 0
);



CREATE TABLE public.games_metadata (
    game_name text NOT NULL,
    rawg_id integer,
    background_image text,
    last_updated timestamp with time zone DEFAULT now() NOT NULL,
    rawg_data jsonb,
    igdb_banner text,
    igdb_assets jsonb
);



CREATE TABLE public.gdpr_requests (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    request_type text NOT NULL,
    status text DEFAULT 'pending'::text NOT NULL,
    requested_at timestamp with time zone DEFAULT now() NOT NULL,
    processed_at timestamp with time zone,
    processed_by uuid,
    notes text DEFAULT ''::text NOT NULL,
    download_url text,
    expires_at timestamp with time zone,
    CONSTRAINT gdpr_requests_request_type_check CHECK ((request_type = ANY (ARRAY['export'::text, 'deletion'::text]))),
    CONSTRAINT gdpr_requests_status_check CHECK ((status = ANY (ARRAY['pending'::text, 'processing'::text, 'completed'::text, 'failed'::text, 'cancelled'::text, 'rejected'::text])))
);



CREATE TABLE public.ghost_approvals (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    requester_id uuid NOT NULL,
    target_user_id uuid NOT NULL,
    reason text NOT NULL,
    status text DEFAULT 'pending'::text NOT NULL,
    approved_by uuid,
    approved_at timestamp with time zone,
    denial_reason text,
    expires_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ghost_approvals_status_check CHECK ((status = ANY (ARRAY['pending'::text, 'approved'::text, 'denied'::text, 'expired'::text])))
);



CREATE TABLE public.ghost_data_access_log (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    session_id uuid NOT NULL,
    field_path text NOT NULL,
    was_unmasked boolean DEFAULT false NOT NULL,
    unmasked_reason text,
    accessed_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.ghost_sessions (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    admin_id uuid NOT NULL,
    target_user_id uuid NOT NULL,
    approval_id uuid,
    token_hash text NOT NULL,
    reason text NOT NULL,
    admin_ip inet,
    admin_user_agent text,
    started_at timestamp with time zone DEFAULT now() NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    ended_at timestamp with time zone,
    last_activity_at timestamp with time zone,
    pages_viewed jsonb DEFAULT '[]'::jsonb NOT NULL,
    fields_unmasked jsonb DEFAULT '[]'::jsonb NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.leaderboard (
    user_id uuid NOT NULL,
    puuid text,
    kd text,
    win_rate text,
    hs_percent text,
    last_updated timestamp with time zone DEFAULT now(),
    latest_match_id text,
    game text DEFAULT 'valorant'::text NOT NULL
);



CREATE TABLE public.licenses (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    license_id text DEFAULT ''::text NOT NULL,
    license_type text NOT NULL,
    status text DEFAULT 'active'::text NOT NULL,
    issued_at timestamp with time zone DEFAULT now(),
    expires_at timestamp with time zone,
    notes text,
    created_at timestamp with time zone DEFAULT now(),
    CONSTRAINT licenses_status_check CHECK ((status = ANY (ARRAY['active'::text, 'suspended'::text, 'revoked'::text]))),
    CONSTRAINT licenses_type_check CHECK ((license_type = ANY (ARRAY['venue_owner'::text, 'organizer'::text, 'broadcaster'::text])))
);



CREATE TABLE public.loyalty_accounts (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    user_id uuid NOT NULL,
    total_points integer DEFAULT 0 NOT NULL,
    lifetime_points integer DEFAULT 0 NOT NULL,
    current_tier text DEFAULT 'bronze'::text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT loyalty_accounts_current_tier_check CHECK ((current_tier = ANY (ARRAY['bronze'::text, 'silver'::text, 'gold'::text, 'platinum'::text])))
);



CREATE TABLE public.loyalty_transactions (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    account_id uuid NOT NULL,
    venue_id uuid NOT NULL,
    type text NOT NULL,
    points integer NOT NULL,
    description text,
    session_id uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT loyalty_transactions_type_check CHECK ((type = ANY (ARRAY['earn'::text, 'redeem'::text, 'bonus'::text, 'expire'::text, 'adjust'::text])))
);



CREATE TABLE public.match_checkins (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    match_id uuid NOT NULL,
    team_id uuid NOT NULL,
    user_id uuid NOT NULL,
    checked_in_at timestamp with time zone DEFAULT now()
);



CREATE TABLE public.match_completed_events (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    match_id uuid NOT NULL,
    winner_id uuid,
    loser_id uuid,
    status text DEFAULT 'pending'::text NOT NULL,
    created_at timestamp with time zone DEFAULT now(),
    processed_at timestamp with time zone,
    error_message text
);



CREATE TABLE public.match_disputes (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    match_id uuid NOT NULL,
    disputed_by_team_id uuid,
    disputed_by_user_id uuid,
    reason text NOT NULL,
    evidence_urls text[] DEFAULT '{}'::text[],
    status text DEFAULT 'pending'::text NOT NULL,
    resolution text,
    resolved_at timestamp with time zone,
    resolved_by uuid,
    created_at timestamp with time zone DEFAULT now(),
    CONSTRAINT match_disputes_status_check CHECK ((status = ANY (ARRAY['pending'::text, 'resolved'::text, 'rejected'::text])))
);



CREATE TABLE public.match_map_veto_actions (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    veto_id uuid NOT NULL,
    match_id uuid NOT NULL,
    team_id uuid NOT NULL,
    action_type text NOT NULL,
    map_id uuid,
    action_number integer NOT NULL,
    created_at timestamp with time zone DEFAULT now(),
    side text,
    tournament_id uuid,
    team_side text,
    created_by uuid
);

ALTER TABLE ONLY public.match_map_veto_actions REPLICA IDENTITY FULL;



CREATE TABLE public.match_map_vetos (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    match_id uuid NOT NULL,
    tournament_id uuid NOT NULL,
    team1_id uuid,
    team2_id uuid,
    status text DEFAULT 'pending'::text,
    current_team_id uuid,
    current_action text,
    current_action_number integer DEFAULT 0,
    turn_started_at timestamp with time zone,
    turn_duration_seconds integer DEFAULT 60,
    team1_banned_maps text[] DEFAULT '{}'::text[],
    team2_banned_maps text[] DEFAULT '{}'::text[],
    selected_map_id uuid,
    started_at timestamp with time zone,
    completed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    team1_picked_maps jsonb DEFAULT '[]'::jsonb,
    team2_picked_maps jsonb DEFAULT '[]'::jsonb,
    team1_link_token text,
    team2_link_token text,
    selected_map_pool text[],
    best_of integer DEFAULT 1,
    stage_id uuid,
    game text DEFAULT 'valorant'::text
);

ALTER TABLE ONLY public.match_map_vetos REPLICA IDENTITY FULL;



CREATE TABLE public.match_messages (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    match_id uuid NOT NULL,
    sender_id uuid NOT NULL,
    sender_name text,
    team_id uuid,
    content text NOT NULL,
    message_type text DEFAULT 'text'::text,
    metadata jsonb,
    created_at timestamp with time zone DEFAULT now()
);



CREATE TABLE public.match_player_stats (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    match_id uuid NOT NULL,
    map_number integer NOT NULL,
    user_id uuid,
    steam64_id text NOT NULL,
    team text NOT NULL,
    player_name text,
    kills integer DEFAULT 0 NOT NULL,
    deaths integer DEFAULT 0 NOT NULL,
    assists integer DEFAULT 0 NOT NULL,
    flash_assists integer DEFAULT 0 NOT NULL,
    team_kills integer DEFAULT 0 NOT NULL,
    suicides integer DEFAULT 0 NOT NULL,
    damage integer DEFAULT 0 NOT NULL,
    utility_damage integer DEFAULT 0 NOT NULL,
    enemies_flashed integer DEFAULT 0 NOT NULL,
    friendlies_flashed integer DEFAULT 0 NOT NULL,
    knife_kills integer DEFAULT 0 NOT NULL,
    headshot_kills integer DEFAULT 0 NOT NULL,
    rounds_played integer DEFAULT 0 NOT NULL,
    bomb_defuses integer DEFAULT 0 NOT NULL,
    bomb_plants integer DEFAULT 0 NOT NULL,
    "1k" integer DEFAULT 0 NOT NULL,
    "2k" integer DEFAULT 0 NOT NULL,
    "3k" integer DEFAULT 0 NOT NULL,
    "4k" integer DEFAULT 0 NOT NULL,
    "5k" integer DEFAULT 0 NOT NULL,
    "1v1" integer DEFAULT 0 NOT NULL,
    "1v2" integer DEFAULT 0 NOT NULL,
    "1v3" integer DEFAULT 0 NOT NULL,
    "1v4" integer DEFAULT 0 NOT NULL,
    "1v5" integer DEFAULT 0 NOT NULL,
    first_kills_t integer DEFAULT 0 NOT NULL,
    first_kills_ct integer DEFAULT 0 NOT NULL,
    first_deaths_t integer DEFAULT 0 NOT NULL,
    first_deaths_ct integer DEFAULT 0 NOT NULL,
    trade_kills integer DEFAULT 0 NOT NULL,
    kast integer DEFAULT 0 NOT NULL,
    score integer DEFAULT 0 NOT NULL,
    mvp integer DEFAULT 0 NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT chk_match_player_stats_team CHECK ((team = ANY (ARRAY['team1'::text, 'team2'::text])))
);



CREATE TABLE public.match_result_reports (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    match_id uuid NOT NULL,
    game_number integer DEFAULT 1 NOT NULL,
    reported_by uuid NOT NULL,
    reported_by_team_id uuid NOT NULL,
    riot_match_id text,
    map_id uuid,
    map_name text,
    team1_score integer DEFAULT 0 NOT NULL,
    team2_score integer DEFAULT 0 NOT NULL,
    winner_team_id uuid,
    match_data jsonb,
    status text DEFAULT 'pending'::text NOT NULL,
    responded_by uuid,
    responded_at timestamp with time zone,
    dispute_reason text,
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    screenshot_urls jsonb DEFAULT '[]'::jsonb,
    comment text,
    CONSTRAINT match_result_reports_status_check CHECK ((status = ANY (ARRAY['pending'::text, 'accepted'::text, 'disputed'::text])))
);

ALTER TABLE ONLY public.match_result_reports REPLICA IDENTITY FULL;



CREATE TABLE public.match_time_proposals (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    match_id uuid NOT NULL,
    proposed_by uuid NOT NULL,
    proposed_time timestamp with time zone NOT NULL,
    status text DEFAULT 'pending'::text,
    created_at timestamp with time zone DEFAULT now(),
    responded_at timestamp with time zone
);



CREATE TABLE public.member_packages (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    member_id uuid NOT NULL,
    package_id uuid NOT NULL,
    hours_total numeric(6,1) NOT NULL,
    hours_remaining numeric(6,1) NOT NULL,
    purchased_at timestamp with time zone DEFAULT now() NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    status text DEFAULT 'active'::text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT member_packages_hours_remaining_check CHECK ((hours_remaining >= (0)::numeric)),
    CONSTRAINT member_packages_hours_total_check CHECK ((hours_total > (0)::numeric)),
    CONSTRAINT member_packages_status_check CHECK ((status = ANY (ARRAY['active'::text, 'expired'::text, 'depleted'::text])))
);



CREATE TABLE public.members (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    user_id uuid,
    display_name text NOT NULL,
    email text,
    phone text,
    avatar_url text,
    balance numeric(10,2) DEFAULT 0 NOT NULL,
    loyalty_points integer DEFAULT 0 NOT NULL,
    loyalty_tier text DEFAULT 'bronze'::text NOT NULL,
    total_hours numeric(10,1) DEFAULT 0 NOT NULL,
    total_spent numeric(10,2) DEFAULT 0 NOT NULL,
    total_sessions integer DEFAULT 0 NOT NULL,
    is_banned boolean DEFAULT false NOT NULL,
    ban_reason text,
    banned_at timestamp with time zone,
    notes text,
    date_of_birth date,
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    CONSTRAINT members_loyalty_tier_check CHECK ((loyalty_tier = ANY (ARRAY['bronze'::text, 'silver'::text, 'gold'::text, 'platinum'::text])))
);



CREATE TABLE public.moderation_queue (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    content_type text NOT NULL,
    content_id uuid NOT NULL,
    field_name text DEFAULT ''::text NOT NULL,
    content_text text,
    content_url text,
    reported_by uuid,
    reported_reason text DEFAULT ''::text,
    status text DEFAULT 'pending'::text NOT NULL,
    reviewed_by uuid,
    reviewed_at timestamp with time zone,
    review_notes text DEFAULT ''::text,
    auto_flagged boolean DEFAULT false NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT moderation_queue_content_type_check CHECK ((content_type = ANY (ARRAY['tournament'::text, 'team'::text, 'profile'::text, 'match_evidence'::text]))),
    CONSTRAINT moderation_queue_status_check CHECK ((status = ANY (ARRAY['pending'::text, 'approved'::text, 'rejected'::text])))
);



CREATE TABLE public.notification_preferences (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    user_id uuid NOT NULL,
    station_offline boolean DEFAULT true NOT NULL,
    low_balance boolean DEFAULT true NOT NULL,
    new_booking boolean DEFAULT true NOT NULL,
    order_ready boolean DEFAULT true NOT NULL,
    session_ending boolean DEFAULT true NOT NULL,
    staff_clock boolean DEFAULT false NOT NULL,
    tamper_alert boolean DEFAULT true NOT NULL,
    walk_in_queue boolean DEFAULT true NOT NULL,
    daily_summary boolean DEFAULT false NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.notifications (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    user_id uuid NOT NULL,
    title text NOT NULL,
    message text NOT NULL,
    type public.notification_type DEFAULT 'info'::public.notification_type,
    is_read boolean DEFAULT false,
    data jsonb DEFAULT '{}'::jsonb,
    created_at timestamp with time zone DEFAULT now(),
    link text
);



CREATE TABLE public.operations_audit_log (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    admin_id uuid NOT NULL,
    impersonated_user_id uuid,
    action_string text NOT NULL,
    target_id text,
    resource_type text NOT NULL,
    ip_address inet,
    user_agent text,
    request_method text,
    request_path text,
    severity text DEFAULT 'low'::text NOT NULL,
    data_diff jsonb DEFAULT '{}'::jsonb NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.organization_albums (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    organization_id uuid NOT NULL,
    title text NOT NULL,
    description text,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL
);



CREATE TABLE public.organization_media (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    organization_id uuid NOT NULL,
    url text NOT NULL,
    type text DEFAULT 'image'::text NOT NULL,
    caption text,
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    album_id uuid
);



CREATE TABLE public.organization_staff (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    organization_id uuid NOT NULL,
    user_id uuid NOT NULL,
    role text DEFAULT 'mod'::text NOT NULL,
    permissions text[] DEFAULT '{}'::text[] NOT NULL,
    status text DEFAULT 'pending'::text NOT NULL,
    assigned_by uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    accepted_at timestamp with time zone,
    responded_at timestamp with time zone
);



CREATE TABLE public.organizations (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    owner_id uuid NOT NULL,
    name text NOT NULL,
    slug text NOT NULL,
    logo_url text,
    banner_url text,
    description text,
    social_links jsonb DEFAULT '{}'::jsonb,
    is_verified boolean DEFAULT false,
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    subscription_tier text DEFAULT 'free'::text,
    settings jsonb DEFAULT '{}'::jsonb,
    CONSTRAINT organizations_subscription_tier_check CHECK ((subscription_tier = ANY (ARRAY['free'::text, 'basic'::text, 'pro'::text, 'enterprise'::text])))
);



CREATE TABLE public.partner_applications (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    company_name text NOT NULL,
    company_website text NOT NULL,
    company_size text NOT NULL,
    industry text NOT NULL,
    contact_name text NOT NULL,
    contact_email text NOT NULL,
    contact_phone text,
    contact_title text,
    partnership_tier text DEFAULT 'standard'::text NOT NULL,
    partnership_goals text[] DEFAULT '{}'::text[],
    budget_range text,
    message text,
    how_heard text,
    status text DEFAULT 'pending'::text NOT NULL,
    admin_notes text,
    reviewed_by uuid,
    reviewed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    invitation_email text,
    approved_sponsor_id uuid,
    approved_by uuid,
    approved_at timestamp with time zone,
    rejected_by uuid,
    rejected_at timestamp with time zone,
    rejection_reason text,
    CONSTRAINT partner_applications_budget_range_check CHECK ((budget_range = ANY (ARRAY['under_1k'::text, '1k_5k'::text, '5k_15k'::text, '15k_50k'::text, '50k_plus'::text, 'undecided'::text]))),
    CONSTRAINT partner_applications_company_size_check CHECK ((company_size = ANY (ARRAY['startup'::text, 'small'::text, 'medium'::text, 'large'::text, 'enterprise'::text]))),
    CONSTRAINT partner_applications_partnership_tier_check CHECK ((partnership_tier = ANY (ARRAY['radiant'::text, 'ascendant'::text, 'diamond'::text, 'standard'::text]))),
    CONSTRAINT partner_applications_status_check CHECK ((status = ANY (ARRAY['pending'::text, 'reviewing'::text, 'approved'::text, 'rejected'::text, 'archived'::text])))
);

ALTER TABLE ONLY public.partner_applications FORCE ROW LEVEL SECURITY;



CREATE TABLE public.partner_sponsor_invitations (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    sponsor_id uuid NOT NULL,
    email text NOT NULL,
    role text DEFAULT 'owner'::text NOT NULL,
    requires_password_setup boolean DEFAULT false NOT NULL,
    token_hash text NOT NULL,
    status text DEFAULT 'pending'::text NOT NULL,
    invited_by uuid NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    accepted_at timestamp with time zone,
    revoked_at timestamp with time zone,
    accepted_by_user_id uuid,
    delivered_at timestamp with time zone,
    delivery_attempts integer DEFAULT 0 NOT NULL,
    delivery_error_code text,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT partner_sponsor_invitations_role_check CHECK ((role = ANY (ARRAY['owner'::text, 'viewer'::text]))),
    CONSTRAINT partner_sponsor_invitations_status_check CHECK ((status = ANY (ARRAY['pending'::text, 'accepted'::text, 'revoked'::text, 'delivery_failed'::text, 'expired'::text])))
);

ALTER TABLE ONLY public.partner_sponsor_invitations FORCE ROW LEVEL SECURITY;



CREATE TABLE public.player_balance (
    user_id uuid NOT NULL,
    balance numeric(10,2) DEFAULT 0 NOT NULL,
    currency text DEFAULT 'GBP'::text NOT NULL,
    updated_at timestamp with time zone DEFAULT now()
);



CREATE TABLE public.player_steam_accounts (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    steam64_id text NOT NULL,
    steam_name text,
    avatar_url text,
    profile_url text,
    linked_at timestamp with time zone DEFAULT now() NOT NULL,
    verified boolean DEFAULT false NOT NULL
);



CREATE TABLE public.pos_orders (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    session_id uuid,
    member_id uuid,
    station_id text,
    items jsonb DEFAULT '[]'::jsonb NOT NULL,
    subtotal numeric(10,2) DEFAULT 0 NOT NULL,
    tax numeric(10,2) DEFAULT 0 NOT NULL,
    discount numeric(10,2) DEFAULT 0 NOT NULL,
    total numeric(10,2) DEFAULT 0 NOT NULL,
    payment_method text DEFAULT 'cash'::text,
    status text DEFAULT 'pending'::text NOT NULL,
    notes text,
    created_by uuid NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT pos_orders_payment_method_check CHECK ((payment_method = ANY (ARRAY['cash'::text, 'card'::text, 'balance'::text, 'split'::text]))),
    CONSTRAINT pos_orders_status_check CHECK ((status = ANY (ARRAY['pending'::text, 'preparing'::text, 'ready'::text, 'delivered'::text, 'cancelled'::text])))
);



CREATE TABLE public.profiles (
    id uuid NOT NULL,
    username text NOT NULL,
    full_name text NOT NULL,
    avatar_url text,
    bio text,
    email text NOT NULL,
    role public.app_role DEFAULT 'casual'::public.app_role,
    is_admin boolean DEFAULT false,
    admin_roles text[] DEFAULT '{}'::text[],
    is_suspended boolean DEFAULT false,
    suspension_reason text,
    suspension_until timestamp with time zone,
    is_verified boolean DEFAULT false,
    verification_status public.verification_status DEFAULT 'unverified'::public.verification_status,
    social_links jsonb DEFAULT '{}'::jsonb,
    last_login timestamp with time zone,
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    base_role character varying(20) DEFAULT 'casual'::character varying,
    card_image_url text,
    slug text,
    banner_url text,
    date_of_birth date,
    riot_tag text,
    steam_tag text,
    country_code text,
    license_id uuid,
    suspension_type text,
    location text,
    settings jsonb DEFAULT '{}'::jsonb,
    CONSTRAINT profiles_country_code_check CHECK (((country_code IS NULL) OR ((length(country_code) = 2) AND (country_code = upper(country_code)))))
);



CREATE TABLE public.public_tool_bracket_versions (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    bracket_id uuid NOT NULL,
    version_number integer DEFAULT 1 NOT NULL,
    status text DEFAULT 'active'::text NOT NULL,
    graph_json jsonb NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT public_tool_bracket_versions_status_check CHECK ((status = ANY (ARRAY['active'::text, 'archived'::text])))
);



CREATE TABLE public.public_tool_brackets (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    owner_user_id uuid NOT NULL,
    title text NOT NULL,
    format text NOT NULL,
    best_of integer DEFAULT 1 NOT NULL,
    status text DEFAULT 'draft'::text NOT NULL,
    visibility text DEFAULT 'private'::text NOT NULL,
    share_token text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT public_tool_brackets_best_of_check CHECK ((best_of = ANY (ARRAY[1, 3, 5]))),
    CONSTRAINT public_tool_brackets_format_check CHECK ((format = ANY (ARRAY['single_elimination'::text, 'double_elimination'::text]))),
    CONSTRAINT public_tool_brackets_status_check CHECK ((status = ANY (ARRAY['draft'::text, 'active'::text, 'completed'::text]))),
    CONSTRAINT public_tool_brackets_visibility_check CHECK ((visibility = ANY (ARRAY['private'::text, 'unlisted'::text])))
);



CREATE TABLE public.public_veto_actions (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    session_id uuid NOT NULL,
    team_id uuid NOT NULL,
    team_side text NOT NULL,
    action_type text NOT NULL,
    map_id text NOT NULL,
    action_number integer NOT NULL,
    side text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT public_veto_actions_action_type_check CHECK ((action_type = ANY (ARRAY['ban'::text, 'pick'::text, 'pick_side'::text]))),
    CONSTRAINT public_veto_actions_team_side_check CHECK ((team_side = ANY (ARRAY['team1'::text, 'team2'::text])))
);



CREATE TABLE public.public_veto_sessions (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    game text NOT NULL,
    best_of integer NOT NULL,
    team1_name text NOT NULL,
    team2_name text NOT NULL,
    team1_id uuid DEFAULT gen_random_uuid() NOT NULL,
    team2_id uuid DEFAULT gen_random_uuid() NOT NULL,
    status text DEFAULT 'in_progress'::text NOT NULL,
    current_team_id uuid,
    current_action text,
    current_action_number integer DEFAULT 1 NOT NULL,
    team1_banned_maps text[] DEFAULT '{}'::text[] NOT NULL,
    team2_banned_maps text[] DEFAULT '{}'::text[] NOT NULL,
    team1_picked_maps jsonb DEFAULT '[]'::jsonb NOT NULL,
    team2_picked_maps jsonb DEFAULT '[]'::jsonb NOT NULL,
    selected_map_id text,
    selected_map_pool text[] DEFAULT '{}'::text[] NOT NULL,
    host_token text NOT NULL,
    team1_token text NOT NULL,
    team2_token text NOT NULL,
    started_at timestamp with time zone DEFAULT now() NOT NULL,
    completed_at timestamp with time zone,
    expires_at timestamp with time zone DEFAULT (now() + '24:00:00'::interval) NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT public_veto_sessions_best_of_check CHECK ((best_of = ANY (ARRAY[1, 3, 5]))),
    CONSTRAINT public_veto_sessions_current_action_check CHECK ((current_action = ANY (ARRAY['ban'::text, 'pick'::text, 'pick_side'::text]))),
    CONSTRAINT public_veto_sessions_status_check CHECK ((status = ANY (ARRAY['in_progress'::text, 'completed'::text, 'cancelled'::text])))
);



CREATE TABLE public.report_run_log (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    schedule_id uuid NOT NULL,
    status text DEFAULT 'running'::text NOT NULL,
    started_at timestamp with time zone DEFAULT now() NOT NULL,
    completed_at timestamp with time zone,
    row_count integer,
    file_size_bytes bigint,
    error_message text,
    download_url text,
    triggered_by text DEFAULT 'schedule'::text NOT NULL
);



CREATE TABLE public.report_schedules (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    name text NOT NULL,
    report_type text NOT NULL,
    frequency text NOT NULL,
    day_of_week integer,
    day_of_month integer,
    time_of_day time without time zone DEFAULT '08:00:00'::time without time zone NOT NULL,
    recipients text[] DEFAULT '{}'::text[] NOT NULL,
    format text DEFAULT 'csv'::text NOT NULL,
    filters jsonb DEFAULT '{}'::jsonb NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    last_run_at timestamp with time zone,
    next_run_at timestamp with time zone,
    created_by uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.reviews (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    reviewer_id uuid NOT NULL,
    reviewee_id uuid,
    venue_id uuid,
    tournament_id uuid,
    rating integer NOT NULL,
    title character varying(200),
    comment text,
    review_type character varying(20) NOT NULL,
    is_verified boolean DEFAULT false,
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    CONSTRAINT reviews_rating_check CHECK (((rating >= 1) AND (rating <= 5))),
    CONSTRAINT reviews_review_type_check CHECK (((review_type)::text = ANY (ARRAY[('user'::character varying)::text, ('venue'::character varying)::text, ('tournament'::character varying)::text])))
);



CREATE TABLE public.revoked_sessions (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    revoked_at timestamp with time zone DEFAULT now() NOT NULL,
    revoked_by uuid NOT NULL,
    reason text,
    expires_at timestamp with time zone NOT NULL
);



CREATE TABLE public.riot_accounts (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    puuid text NOT NULL,
    game_name text NOT NULL,
    tag_line text NOT NULL,
    region text DEFAULT 'asia'::text,
    access_token text,
    refresh_token text,
    token_expires_at timestamp with time zone,
    linked_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now()
);



CREATE TABLE public.schemaversions (
    schemaversionsid integer NOT NULL,
    scriptname character varying(255) NOT NULL,
    applied timestamp without time zone NOT NULL
);



CREATE SEQUENCE public.schemaversions_schemaversionsid_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;



ALTER SEQUENCE public.schemaversions_schemaversionsid_seq OWNED BY public.schemaversions.schemaversionsid;



CREATE TABLE public.session_invoices (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    session_id uuid,
    member_id uuid,
    station_id text,
    line_items jsonb DEFAULT '[]'::jsonb NOT NULL,
    subtotal numeric(10,2) DEFAULT 0 NOT NULL,
    tax numeric(10,2) DEFAULT 0 NOT NULL,
    discount numeric(10,2) DEFAULT 0 NOT NULL,
    total numeric(10,2) DEFAULT 0 NOT NULL,
    payment_method text,
    status text DEFAULT 'pending'::text NOT NULL,
    paid_at timestamp with time zone,
    created_by uuid,
    notes text,
    created_at timestamp with time zone DEFAULT now(),
    CONSTRAINT session_invoices_payment_method_check CHECK ((payment_method = ANY (ARRAY['cash'::text, 'card'::text, 'wallet'::text, 'package'::text, 'free'::text, 'split'::text]))),
    CONSTRAINT session_invoices_status_check CHECK ((status = ANY (ARRAY['pending'::text, 'paid'::text, 'voided'::text, 'refunded'::text, 'partial_refund'::text])))
);



CREATE TABLE public.session_refunds (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    session_id uuid NOT NULL,
    amount numeric(10,2) NOT NULL,
    method text NOT NULL,
    reason text NOT NULL,
    wallet_txn_id uuid,
    refunded_by uuid NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT session_refunds_amount_check CHECK ((amount > (0)::numeric)),
    CONSTRAINT session_refunds_method_check CHECK ((method = ANY (ARRAY['wallet'::text, 'cash'::text])))
);



CREATE TABLE public.sponsor_accounts (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid,
    sponsor_id uuid,
    role text DEFAULT 'owner'::text NOT NULL,
    invited_by uuid,
    invited_at timestamp with time zone DEFAULT now(),
    accepted_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now(),
    onboarding_meta jsonb DEFAULT '{"completed": false, "current_step": 0}'::jsonb,
    status text DEFAULT 'active'::text NOT NULL,
    invitation_id uuid,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT sponsor_accounts_role_check CHECK ((role = ANY (ARRAY['owner'::text, 'viewer'::text]))),
    CONSTRAINT sponsor_accounts_status_check CHECK ((status = ANY (ARRAY['active'::text, 'revoked'::text])))
);

ALTER TABLE ONLY public.sponsor_accounts FORCE ROW LEVEL SECURITY;



CREATE TABLE public.sponsor_analytics_events (
    event_sequence bigint NOT NULL,
    event_id uuid NOT NULL,
    sponsor_id uuid NOT NULL,
    audience_id uuid NOT NULL,
    identity_kind text NOT NULL,
    event_type text NOT NULL,
    placement text NOT NULL,
    tournament_id uuid,
    page_path text,
    country_code text,
    country_provenance text NOT NULL,
    age_band text,
    age_provenance text NOT NULL,
    schema_version smallint DEFAULT 1 NOT NULL,
    received_at timestamp with time zone DEFAULT now() NOT NULL,
    event_date_utc date NOT NULL,
    CONSTRAINT sponsor_analytics_events_age_band_check CHECK ((age_band = ANY (ARRAY['13_17'::text, '18_24'::text, '25_34'::text, '35_44'::text, '45_54'::text, '55_plus'::text]))),
    CONSTRAINT sponsor_analytics_events_age_provenance_check CHECK ((age_provenance = ANY (ARRAY['profile_self_reported'::text, 'unknown'::text]))),
    CONSTRAINT sponsor_analytics_events_check CHECK (((country_code IS NULL) = (country_provenance = 'unknown'::text))),
    CONSTRAINT sponsor_analytics_events_check1 CHECK (((age_band IS NULL) = (age_provenance = 'unknown'::text))),
    CONSTRAINT sponsor_analytics_events_country_code_check CHECK (((country_code IS NULL) OR (country_code ~ '^[A-Z]{2}$'::text))),
    CONSTRAINT sponsor_analytics_events_country_provenance_check CHECK ((country_provenance = ANY (ARRAY['profile_self_reported'::text, 'geoip'::text, 'unknown'::text]))),
    CONSTRAINT sponsor_analytics_events_event_type_check CHECK ((event_type = ANY (ARRAY['impression'::text, 'click'::text]))),
    CONSTRAINT sponsor_analytics_events_identity_kind_check CHECK ((identity_kind = ANY (ARRAY['authenticated'::text, 'anonymous'::text])))
);

ALTER TABLE ONLY public.sponsor_analytics_events FORCE ROW LEVEL SECURITY;



ALTER TABLE public.sponsor_analytics_events ALTER COLUMN event_sequence ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.sponsor_analytics_events_event_sequence_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE public.sponsor_analytics_exports (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    sponsor_id uuid NOT NULL,
    requested_by uuid NOT NULL,
    report_type text NOT NULL,
    period_days integer NOT NULL,
    status text DEFAULT 'pending'::text NOT NULL,
    file_path text,
    signed_url text,
    url_expires_at timestamp with time zone,
    requested_at timestamp with time zone DEFAULT now() NOT NULL,
    completed_at timestamp with time zone,
    error_message text,
    CONSTRAINT sponsor_analytics_exports_period_days_check CHECK ((period_days = ANY (ARRAY[7, 30, 90]))),
    CONSTRAINT sponsor_analytics_exports_report_type_check CHECK ((report_type = ANY (ARRAY['summary'::text, 'performance'::text, 'placements'::text, 'content'::text, 'devices'::text, 'full'::text]))),
    CONSTRAINT sponsor_analytics_exports_status_check CHECK ((status = ANY (ARRAY['pending'::text, 'processing'::text, 'completed'::text, 'failed'::text, 'expired'::text])))
);

ALTER TABLE ONLY public.sponsor_analytics_exports FORCE ROW LEVEL SECURITY;



CREATE TABLE public.sponsor_audience_daily_facts (
    sponsor_id uuid NOT NULL,
    fact_date date NOT NULL,
    audience_id uuid NOT NULL,
    event_type text NOT NULL,
    event_count bigint NOT NULL,
    first_event_sequence bigint NOT NULL,
    last_event_sequence bigint NOT NULL,
    country_code text,
    country_provenance text NOT NULL,
    age_band text,
    age_provenance text NOT NULL,
    CONSTRAINT sponsor_audience_daily_facts_age_band_check CHECK ((age_band = ANY (ARRAY['13_17'::text, '18_24'::text, '25_34'::text, '35_44'::text, '45_54'::text, '55_plus'::text]))),
    CONSTRAINT sponsor_audience_daily_facts_age_provenance_check CHECK ((age_provenance = ANY (ARRAY['profile_self_reported'::text, 'unknown'::text]))),
    CONSTRAINT sponsor_audience_daily_facts_country_provenance_check CHECK ((country_provenance = ANY (ARRAY['profile_self_reported'::text, 'geoip'::text, 'unknown'::text]))),
    CONSTRAINT sponsor_audience_daily_facts_event_count_check CHECK ((event_count > 0)),
    CONSTRAINT sponsor_audience_daily_facts_event_type_check CHECK ((event_type = ANY (ARRAY['impression'::text, 'click'::text])))
);

ALTER TABLE ONLY public.sponsor_audience_daily_facts FORCE ROW LEVEL SECURITY;



CREATE TABLE public.sponsor_audience_identities (
    sponsor_id uuid NOT NULL,
    identity_lookup bytea NOT NULL,
    identity_key_version smallint NOT NULL,
    identity_kind text NOT NULL,
    audience_id uuid DEFAULT gen_random_uuid() NOT NULL,
    first_seen_at timestamp with time zone DEFAULT now() NOT NULL,
    last_seen_at timestamp with time zone DEFAULT now() NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    CONSTRAINT sponsor_audience_identities_check CHECK ((expires_at > last_seen_at)),
    CONSTRAINT sponsor_audience_identities_identity_kind_check CHECK ((identity_kind = ANY (ARRAY['authenticated'::text, 'anonymous'::text]))),
    CONSTRAINT sponsor_audience_identities_identity_lookup_check CHECK ((octet_length(identity_lookup) = 32))
);

ALTER TABLE ONLY public.sponsor_audience_identities FORCE ROW LEVEL SECURITY;



CREATE TABLE public.sponsor_content_daily_stats (
    sponsor_id uuid NOT NULL,
    stat_date date NOT NULL,
    tournament_id uuid,
    page_path text,
    impressions bigint DEFAULT 0 NOT NULL,
    clicks bigint DEFAULT 0 NOT NULL
);

ALTER TABLE ONLY public.sponsor_content_daily_stats FORCE ROW LEVEL SECURITY;



CREATE TABLE public.sponsor_daily_totals (
    sponsor_id uuid NOT NULL,
    stat_date date NOT NULL,
    impressions bigint DEFAULT 0 NOT NULL,
    clicks bigint DEFAULT 0 NOT NULL
);

ALTER TABLE ONLY public.sponsor_daily_totals FORCE ROW LEVEL SECURITY;



CREATE TABLE public.sponsor_device_daily_stats (
    sponsor_id uuid NOT NULL,
    stat_date date NOT NULL,
    device_class text NOT NULL,
    impressions bigint DEFAULT 0 NOT NULL,
    clicks bigint DEFAULT 0 NOT NULL,
    CONSTRAINT sponsor_device_daily_stats_device_class_check CHECK ((device_class = ANY (ARRAY['mobile-web'::text, 'desktop-web'::text, 'tablet-web'::text, 'unknown-web'::text])))
);

ALTER TABLE ONLY public.sponsor_device_daily_stats FORCE ROW LEVEL SECURITY;



CREATE TABLE public.sponsor_impressions (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    sponsor_id uuid,
    event_type text NOT NULL,
    created_at timestamp with time zone DEFAULT now(),
    page_url text,
    metadata jsonb DEFAULT '{}'::jsonb,
    visitor_id text,
    tournament_id uuid,
    CONSTRAINT sponsor_impressions_event_type_check CHECK ((event_type = ANY (ARRAY['impression'::text, 'click'::text])))
);



CREATE TABLE public.sponsor_placement_daily_stats (
    sponsor_id uuid NOT NULL,
    stat_date date NOT NULL,
    placement text NOT NULL,
    impressions bigint DEFAULT 0 NOT NULL,
    clicks bigint DEFAULT 0 NOT NULL
);

ALTER TABLE ONLY public.sponsor_placement_daily_stats FORCE ROW LEVEL SECURITY;



CREATE TABLE public.sponsor_placements (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    sponsor_id uuid NOT NULL,
    tournament_id uuid,
    placement_zone text NOT NULL,
    banner_url text,
    logo_url text,
    headline text,
    cta_text text,
    cta_url text,
    priority integer DEFAULT 0 NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    assigned_by uuid,
    starts_at timestamp with time zone,
    ends_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT sponsor_placements_placement_zone_check CHECK ((placement_zone = ANY (ARRAY['homepage_ticker'::text, 'partner_showcase'::text, 'sidebar_partner'::text, 'wide_partner'::text, 'card_badge'::text, 'partner_logo'::text])))
);

ALTER TABLE ONLY public.sponsor_placements FORCE ROW LEVEL SECURITY;



CREATE TABLE public.sponsors (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    name text NOT NULL,
    tagline text,
    description text,
    website_url text NOT NULL,
    logo_url text,
    banner_image_url text,
    accent_color text DEFAULT '#8b5cf6'::text,
    tier text DEFAULT 'standard'::text,
    placement text[] DEFAULT '{banner}'::text[],
    cta_text text DEFAULT 'Learn More'::text,
    discount_text text,
    is_active boolean DEFAULT true,
    priority integer DEFAULT 0,
    start_date timestamp with time zone,
    end_date timestamp with time zone,
    created_at timestamp with time zone DEFAULT now(),
    gallery_images text[] DEFAULT '{}'::text[],
    tier_features jsonb DEFAULT '{}'::jsonb,
    detail_deck_url text,
    placement_assets jsonb DEFAULT '{}'::jsonb,
    CONSTRAINT sponsors_tier_check CHECK ((tier = ANY (ARRAY['radiant'::text, 'ascendant'::text, 'diamond'::text, 'standard'::text])))
);



CREATE TABLE public.staff_audit_log (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    organization_id uuid,
    actor_id uuid NOT NULL,
    action text NOT NULL,
    target_type text,
    target_id text,
    details jsonb DEFAULT '{}'::jsonb,
    ip_address text,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.staff_permissions (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    role text NOT NULL,
    permission text NOT NULL
);



CREATE TABLE public.staff_shifts (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    staff_id uuid NOT NULL,
    staff_name text,
    clocked_in timestamp with time zone DEFAULT now() NOT NULL,
    clocked_out timestamp with time zone,
    total_minutes numeric(8,2),
    notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.staff_tournament_assignments (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    organization_staff_id uuid NOT NULL,
    tournament_id uuid NOT NULL,
    assigned_by uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    permissions text[]
);



CREATE TABLE public.stage_participants (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    stage_id uuid NOT NULL,
    seed integer,
    status text DEFAULT 'pending'::text,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    team_id uuid NOT NULL
);



CREATE TABLE public.station_health_snapshots (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    station_id text NOT NULL,
    cpu_temp numeric(5,2),
    gpu_temp numeric(5,2),
    ram_pct numeric(5,2),
    disk_pct numeric(5,2),
    latency_ms integer,
    recorded_at timestamp with time zone DEFAULT now()
);



CREATE TABLE public.system_config (
    key text NOT NULL,
    value jsonb DEFAULT 'null'::jsonb NOT NULL,
    category text DEFAULT 'general'::text NOT NULL,
    label text NOT NULL,
    description text DEFAULT ''::text NOT NULL,
    data_type text DEFAULT 'boolean'::text NOT NULL,
    is_kill_switch boolean DEFAULT false NOT NULL,
    is_sensitive boolean DEFAULT false NOT NULL,
    updated_by uuid,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT system_config_data_type_check CHECK ((data_type = ANY (ARRAY['boolean'::text, 'number'::text, 'string'::text, 'json'::text])))
);



CREATE TABLE public.system_settings (
    key text NOT NULL,
    description text,
    updated_by uuid,
    updated_at timestamp with time zone DEFAULT now(),
    value text DEFAULT ''::text NOT NULL,
    category text DEFAULT 'general'::text NOT NULL,
    label text DEFAULT ''::text NOT NULL,
    data_type text DEFAULT 'string'::text NOT NULL,
    is_sensitive boolean DEFAULT false NOT NULL
);



CREATE TABLE public.team_invitations (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    team_id uuid NOT NULL,
    roster_id uuid,
    invited_user_id uuid NOT NULL,
    invited_email text,
    invited_by_user_id uuid NOT NULL,
    status text DEFAULT 'pending'::text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    responded_at timestamp with time zone,
    invited_by uuid,
    message text,
    CONSTRAINT team_invitations_status_check CHECK ((status = ANY (ARRAY['pending'::text, 'accepted'::text, 'declined'::text, 'expired'::text])))
);



CREATE TABLE public.team_members (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    team_id uuid NOT NULL,
    user_id uuid NOT NULL,
    role public.team_member_role DEFAULT 'member'::public.team_member_role,
    joined_at timestamp with time zone DEFAULT now(),
    is_active boolean DEFAULT true,
    display_order integer DEFAULT 0 NOT NULL
);



CREATE TABLE public.team_roster_members (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    roster_id uuid NOT NULL,
    user_id uuid NOT NULL,
    role text,
    is_active boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    is_starter boolean DEFAULT true,
    roster_role public.roster_member_role DEFAULT 'starter'::public.roster_member_role NOT NULL,
    display_order integer DEFAULT 0 NOT NULL
);



CREATE TABLE public.team_rosters (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    team_id uuid NOT NULL,
    name text NOT NULL,
    game text NOT NULL,
    format text,
    team_size integer NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT team_rosters_team_size_check CHECK (((team_size >= 1) AND (team_size <= 10)))
);



CREATE TABLE public.teams (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    name text NOT NULL,
    tag text NOT NULL,
    description text,
    game text NOT NULL,
    games text[] DEFAULT '{}'::text[],
    game_format text,
    logo_url text,
    banner_url text,
    website_url text,
    social_media jsonb DEFAULT '{}'::jsonb,
    achievements jsonb DEFAULT '{}'::jsonb,
    is_public boolean DEFAULT true,
    is_active boolean DEFAULT true,
    max_members integer DEFAULT 5,
    owner_id uuid NOT NULL,
    stats jsonb DEFAULT '{}'::jsonb,
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    deleted_at timestamp with time zone,
    country_code text,
    is_solo boolean DEFAULT false NOT NULL,
    is_mock boolean DEFAULT false NOT NULL,
    team_kind text DEFAULT 'team'::text NOT NULL,
    CONSTRAINT chk_teams_kind_solo_consistency CHECK ((((team_kind = 'solo'::text) AND (is_solo = true) AND (max_members = 1)) OR (team_kind <> 'solo'::text))),
    CONSTRAINT chk_teams_team_kind CHECK ((team_kind = ANY (ARRAY['team'::text, 'solo'::text, 'mock'::text]))),
    CONSTRAINT teams_country_code_check CHECK (((country_code IS NULL) OR ((length(country_code) = 2) AND (country_code = upper(country_code)))))
);



CREATE TABLE public.tournament_announcements (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    tournament_id uuid NOT NULL,
    sender_id uuid NOT NULL,
    title text NOT NULL,
    content text NOT NULL,
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now()
);



CREATE TABLE public.tournament_bans (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    tournament_id uuid NOT NULL,
    user_id uuid,
    ban_reason text,
    banned_by uuid,
    banned_at timestamp with time zone DEFAULT now(),
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    team_id uuid,
    participant_id uuid,
    is_active boolean DEFAULT true NOT NULL,
    lifted_by uuid,
    lifted_at timestamp with time zone,
    CONSTRAINT tournament_bans_user_or_team CHECK (((((user_id IS NOT NULL))::integer + ((team_id IS NOT NULL))::integer) = 1))
);



CREATE TABLE public.tournament_disputes (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    tournament_id uuid,
    match_id uuid,
    raised_by_user_id uuid NOT NULL,
    team_id uuid,
    title text NOT NULL,
    description text,
    evidence_url text,
    status text DEFAULT 'open'::text NOT NULL,
    assigned_to_user_id uuid,
    resolution_notes text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    dispute_reason text,
    reference_number text
);



CREATE TABLE public.tournament_invitations (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    tournament_id uuid NOT NULL,
    email text NOT NULL,
    code text NOT NULL,
    status text DEFAULT 'draft'::text NOT NULL,
    expires_at timestamp with time zone,
    redeemed_by uuid,
    redeemed_team_id uuid,
    redeemed_at timestamp with time zone,
    sent_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    redeemed_participant_id uuid,
    expiry_job_id text,
    CONSTRAINT chk_tournament_invitations_redeemed_fields CHECK ((((status = 'redeemed'::text) AND (redeemed_at IS NOT NULL) AND (redeemed_by IS NOT NULL) AND ((redeemed_team_id IS NOT NULL) OR (redeemed_participant_id IS NOT NULL))) OR ((status <> 'redeemed'::text) AND (redeemed_at IS NULL)))),
    CONSTRAINT tournament_invitations_check CHECK (((expires_at IS NULL) OR (expires_at > created_at))),
    CONSTRAINT tournament_invitations_check1 CHECK (((status = 'redeemed'::text) = (redeemed_at IS NOT NULL))),
    CONSTRAINT tournament_invitations_email_check CHECK ((email = lower(TRIM(BOTH FROM email)))),
    CONSTRAINT tournament_invitations_status_check CHECK ((status = ANY (ARRAY['draft'::text, 'sent'::text, 'redeemed'::text, 'expired'::text, 'revoked'::text])))
);



CREATE TABLE public.tournament_map_pools (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    tournament_id uuid NOT NULL,
    map_id uuid NOT NULL,
    created_at timestamp with time zone DEFAULT now(),
    game text,
    map_name text
);



CREATE TABLE public.tournament_match_results (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    tournament_id uuid NOT NULL,
    match_id uuid,
    team_id uuid,
    reporter_user_id uuid NOT NULL,
    image_url text,
    comment text,
    status text DEFAULT 'pending'::text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.tournament_participants (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    tournament_id uuid NOT NULL,
    participant_type public.registration_type NOT NULL,
    user_id uuid,
    team_id uuid,
    solo_contact_email text,
    solo_contact_phone text,
    team_name text,
    team_captain_id uuid,
    team_members jsonb DEFAULT '[]'::jsonb,
    team_logo_url text,
    team_contact_email text,
    team_contact_phone text,
    status public.registration_status DEFAULT 'pending'::public.registration_status,
    entry_fee_paid boolean DEFAULT false,
    entry_fee_amount numeric(10,2) DEFAULT 0,
    payment_reference text,
    verified_by uuid,
    verified_at timestamp with time zone,
    verification_notes text,
    rejection_reason text,
    registration_date timestamp with time zone DEFAULT now(),
    check_in_date timestamp with time zone,
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    roster_id uuid,
    roster_name text,
    checked_in_at timestamp with time zone,
    qualified_to_playoff boolean DEFAULT false NOT NULL,
    riot_tag text,
    steam_tag text,
    payment_receipt_url text,
    payment_status text DEFAULT 'not_required'::text,
    payment_rejection_reason text,
    is_mock boolean DEFAULT false NOT NULL,
    source text DEFAULT 'open'::text NOT NULL,
    roster_lineup jsonb,
    CONSTRAINT check_participant_type CHECK ((participant_type = ANY (ARRAY['solo'::public.registration_type, 'team'::public.registration_type]))),
    CONSTRAINT chk_mock_or_user_id CHECK (((is_mock = true) OR (user_id IS NOT NULL))),
    CONSTRAINT chk_tournament_participants_source CHECK ((source = ANY (ARRAY['open'::text, 'invite'::text, 'advancement'::text]))),
    CONSTRAINT tournament_participants_payment_status_check CHECK ((payment_status = ANY (ARRAY['not_required'::text, 'pending'::text, 'approved'::text, 'rejected'::text])))
);



CREATE TABLE public.tournament_stages (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    tournament_id uuid,
    name text NOT NULL,
    sequence_order integer DEFAULT 1,
    advancement_count integer,
    status text DEFAULT 'pending'::text,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    format text DEFAULT 'single_elimination'::text,
    stage_order integer DEFAULT 1,
    capacity integer,
    is_locked boolean DEFAULT false,
    best_of integer DEFAULT 1,
    map_pool uuid[] DEFAULT '{}'::uuid[],
    veto_enabled boolean DEFAULT true,
    config jsonb,
    scheduling_config jsonb DEFAULT '{"round_deadline": null, "checkin_enabled": true, "self_play_enabled": false, "schedule_start_time": null, "checkin_window_minutes": 15, "match_interval_minutes": 75}'::jsonb,
    starts_at timestamp with time zone,
    ends_at timestamp with time zone,
    bo_mode text DEFAULT 'per_stage'::text NOT NULL,
    round_bo_overrides jsonb,
    CONSTRAINT tournament_stages_bo_mode_check CHECK ((bo_mode = ANY (ARRAY['per_stage'::text, 'per_round'::text])))
);



CREATE TABLE public.tournaments (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    name text NOT NULL,
    description text,
    slug text,
    game text NOT NULL,
    max_teams integer NOT NULL,
    min_teams integer DEFAULT 2,
    entry_fee numeric(10,2) DEFAULT 0,
    prize_pool numeric(10,2) DEFAULT 0,
    prize_distribution jsonb DEFAULT '[]'::jsonb,
    start_date timestamp with time zone NOT NULL,
    end_date timestamp with time zone NOT NULL,
    registration_deadline timestamp with time zone NOT NULL,
    check_in_time timestamp with time zone,
    status public.tournament_status DEFAULT 'draft'::public.tournament_status,
    rules text,
    requirements text,
    age_restriction jsonb DEFAULT '{}'::jsonb,
    skill_level text DEFAULT 'all'::text,
    banner_url text,
    logo_url text,
    organizer_id uuid NOT NULL,
    venue_id uuid,
    is_public boolean DEFAULT true,
    is_featured boolean DEFAULT false,
    allow_spectators boolean DEFAULT true,
    stream_url text,
    stats jsonb DEFAULT '{}'::jsonb,
    approved_by uuid,
    approved_at timestamp with time zone,
    rejection_reason text,
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    check_in_required boolean DEFAULT false NOT NULL,
    check_in_deadline timestamp with time zone,
    auto_remove_unchecked boolean DEFAULT true NOT NULL,
    participant_cap integer,
    deleted_at timestamp with time zone,
    team_size integer,
    settings jsonb DEFAULT '{}'::jsonb,
    format text DEFAULT 'Single Elimination'::text,
    rewards text,
    winner_id uuid,
    organization_id uuid,
    check_in_reminder_sent boolean DEFAULT false,
    payment_instructions text,
    region text,
    currency text DEFAULT 'USD'::text,
    server_region text,
    season_id uuid,
    season_role text,
    created_via text DEFAULT 'standalone'::text NOT NULL,
    season_stage_order integer,
    reserved_invite_slots integer DEFAULT 0 NOT NULL,
    invite_expiry_days integer DEFAULT 7 NOT NULL,
    game_mode text,
    checkin_job_id text,
    CONSTRAINT chk_tournaments_invite_expiry_days_positive CHECK (((invite_expiry_days >= 1) AND (invite_expiry_days <= 365))),
    CONSTRAINT chk_tournaments_reserved_invite_slots_nonnegative CHECK ((reserved_invite_slots >= 0)),
    CONSTRAINT tournaments_created_via_check CHECK ((created_via = ANY (ARRAY['standalone'::text, 'season'::text, 'admin'::text]))),
    CONSTRAINT tournaments_season_role_check CHECK ((season_role = ANY (ARRAY['qualifier'::text, 'event'::text, 'regional_final'::text, 'last_chance_qualifier'::text, 'playoff'::text, 'grand_final'::text, 'custom'::text])))
);



CREATE TABLE public.user_roles (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    role text NOT NULL,
    is_active boolean DEFAULT true,
    is_primary boolean DEFAULT false,
    assigned_by uuid,
    assigned_at timestamp with time zone DEFAULT now(),
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    CONSTRAINT user_roles_role_check CHECK ((role = ANY (ARRAY['casual'::text, 'organizer'::text, 'venue_owner'::text, 'admin'::text])))
);



CREATE VIEW public.user_role_summary WITH (security_invoker='true') AS
 SELECT p.id AS user_id,
    p.username,
    p.full_name,
    p.email,
    array_agg(ur.role ORDER BY ur.is_primary DESC, ur.assigned_at) AS roles,
    array_agg(ur.role ORDER BY ur.is_primary DESC, ur.assigned_at) FILTER (WHERE (ur.is_primary = true)) AS primary_roles,
    count(ur.role) AS total_roles
   FROM (public.profiles p
     LEFT JOIN public.user_roles ur ON (((p.id = ur.user_id) AND (ur.is_active = true))))
  GROUP BY p.id, p.username, p.full_name, p.email;



CREATE VIEW public.v_tournament_details AS
 SELECT t.id,
    t.name,
    t.description,
    t.slug,
    t.game,
    t.max_teams,
    t.min_teams,
    t.entry_fee,
    t.prize_pool,
    t.prize_distribution,
    t.start_date,
    t.end_date,
    t.registration_deadline,
    t.check_in_time,
    t.status,
    t.rules,
    t.requirements,
    t.age_restriction,
    t.skill_level,
    t.banner_url,
    t.logo_url,
    t.organizer_id,
    t.venue_id,
    t.is_public,
    t.is_featured,
    t.allow_spectators,
    t.stream_url,
    t.stats,
    t.approved_by,
    t.approved_at,
    t.rejection_reason,
    t.created_at,
    t.updated_at,
    t.check_in_required,
    t.check_in_deadline,
    t.auto_remove_unchecked,
    t.participant_cap,
    t.deleted_at,
    t.team_size,
    t.settings,
    t.format,
    t.rewards,
    t.winner_id,
    t.organization_id,
    t.check_in_reminder_sent,
    (t.venue_id IS NULL) AS is_online,
    o.name AS organization_name,
    o.slug AS organization_slug,
    o.logo_url AS organization_logo,
    o.owner_id AS organizer_owner_id,
    p.username AS organizer_username,
    p.avatar_url AS organizer_avatar,
    tm.name AS winner_team_name,
    ( SELECT count(*) AS count
           FROM public.tournament_participants tp
          WHERE (tp.tournament_id = t.id)) AS participant_count
   FROM (((public.tournaments t
     LEFT JOIN public.organizations o ON ((t.organization_id = o.id)))
     LEFT JOIN public.profiles p ON ((o.owner_id = p.id)))
     LEFT JOIN public.teams tm ON ((t.winner_id = tm.id)));



CREATE TABLE public.venue_announcements (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    venue_id uuid NOT NULL,
    title text NOT NULL,
    body text DEFAULT ''::text NOT NULL,
    type text DEFAULT 'info'::text NOT NULL,
    priority integer DEFAULT 0 NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    starts_at timestamp with time zone,
    expires_at timestamp with time zone,
    created_by uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT venue_announcements_type_check CHECK ((type = ANY (ARRAY['info'::text, 'promo'::text, 'event'::text, 'alert'::text])))
);



CREATE TABLE public.venue_availability (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    date date NOT NULL,
    start_time time without time zone NOT NULL,
    end_time time without time zone NOT NULL,
    available_stations integer NOT NULL,
    price_per_hour numeric(8,2) NOT NULL,
    is_available boolean DEFAULT true,
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now()
);



CREATE TABLE public.venue_availability_snapshot (
    venue_id uuid NOT NULL,
    total_stations integer DEFAULT 0 NOT NULL,
    available integer DEFAULT 0 NOT NULL,
    occupied integer DEFAULT 0 NOT NULL,
    maintenance integer DEFAULT 0 NOT NULL,
    offline integer DEFAULT 0 NOT NULL,
    updated_at timestamp with time zone DEFAULT now()
);



CREATE TABLE public.venue_billing_config (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    zones jsonb DEFAULT '[]'::jsonb NOT NULL,
    packages jsonb DEFAULT '[]'::jsonb NOT NULL,
    vouchers jsonb DEFAULT '[]'::jsonb NOT NULL,
    grace_period_minutes integer DEFAULT 5 NOT NULL,
    currency text DEFAULT 'GBP'::text NOT NULL,
    updated_at timestamp with time zone DEFAULT now()
);



CREATE TABLE public.venue_bookings (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    user_id uuid NOT NULL,
    booking_date date NOT NULL,
    start_time time without time zone NOT NULL,
    end_time time without time zone NOT NULL,
    duration_hours numeric(3,1) NOT NULL,
    stations_booked integer DEFAULT 1 NOT NULL,
    total_amount numeric(10,2) NOT NULL,
    status character varying(20) DEFAULT 'pending'::character varying,
    payment_id uuid,
    special_requests text,
    contact_phone character varying(20),
    contact_email character varying(255),
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    booking_code text,
    code_used_at timestamp with time zone,
    station_preference text,
    station_id text,
    cancelled_at timestamp with time zone,
    cancelled_by uuid,
    cancellation_reason text,
    refund_amount numeric(10,2),
    original_booking_id uuid,
    source text DEFAULT 'web'::text,
    zone_id uuid,
    member_id uuid,
    session_id uuid,
    checked_in_at timestamp with time zone,
    no_show_at timestamp with time zone,
    CONSTRAINT venue_bookings_status_check CHECK (((status)::text = ANY (ARRAY[('pending'::character varying)::text, ('confirmed'::character varying)::text, ('cancelled'::character varying)::text, ('completed'::character varying)::text, ('no_show'::character varying)::text])))
);



CREATE TABLE public.venue_combos (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    venue_id uuid NOT NULL,
    name text NOT NULL,
    description text DEFAULT ''::text NOT NULL,
    items jsonb DEFAULT '[]'::jsonb NOT NULL,
    total_price numeric(10,2) NOT NULL,
    original_price numeric(10,2) DEFAULT 0.00 NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    sort_order integer DEFAULT 0 NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.venue_impressions (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    event_type text NOT NULL,
    user_id uuid,
    session_id text,
    created_at timestamp with time zone DEFAULT now(),
    CONSTRAINT impressions_type_check CHECK ((event_type = ANY (ARRAY['view'::text, 'card_view'::text, 'booking_click'::text, 'contact_click'::text])))
);



CREATE TABLE public.venue_live_status (
    venue_id uuid NOT NULL,
    seats_total integer DEFAULT 0 NOT NULL,
    seats_occupied integer DEFAULT 0 NOT NULL,
    is_open boolean DEFAULT false NOT NULL,
    updated_at timestamp with time zone DEFAULT now()
);

ALTER TABLE ONLY public.venue_live_status REPLICA IDENTITY FULL;



CREATE TABLE public.venue_loyalty_config (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    venue_id uuid NOT NULL,
    points_per_hour integer DEFAULT 10 NOT NULL,
    bonus_multiplier numeric(3,1) DEFAULT 1.0 NOT NULL,
    min_session_minutes integer DEFAULT 30 NOT NULL,
    tiers jsonb DEFAULT '[{"name": "bronze", "perks": "Standard rates", "min_points": 0, "multiplier": 1.0}, {"name": "silver", "perks": "5% discount on sessions", "min_points": 500, "multiplier": 1.2}, {"name": "gold", "perks": "10% discount + priority booking", "min_points": 2000, "multiplier": 1.5}, {"name": "platinum", "perks": "15% discount + free hour monthly", "min_points": 5000, "multiplier": 2.0}]'::jsonb NOT NULL,
    rewards jsonb DEFAULT '[{"id": "free_30min", "name": "Free 30 Minutes", "type": "time", "value": 30, "points_cost": 100}, {"id": "free_1hr", "name": "Free 1 Hour", "type": "time", "value": 60, "points_cost": 180}, {"id": "wallet_5", "name": "$5 Wallet Credit", "type": "wallet_credit", "value": 5, "points_cost": 200}]'::jsonb NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);



CREATE TABLE public.venue_menu_items (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    venue_id uuid NOT NULL,
    name text NOT NULL,
    category text DEFAULT 'other'::text NOT NULL,
    price numeric(10,2) DEFAULT 0.00 NOT NULL,
    is_available boolean DEFAULT true NOT NULL,
    sort_order integer DEFAULT 0 NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    image_url text,
    description text DEFAULT ''::text,
    stock_count integer,
    low_stock_threshold integer DEFAULT 5,
    CONSTRAINT venue_menu_items_category_check CHECK ((category = ANY (ARRAY['food'::text, 'drink'::text, 'snack'::text, 'other'::text])))
);



CREATE TABLE public.venue_packages (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    name text NOT NULL,
    description text DEFAULT ''::text,
    hours numeric(6,1) NOT NULL,
    price numeric(10,2) NOT NULL,
    original_price numeric(10,2),
    validity_days integer DEFAULT 30 NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    sort_order integer DEFAULT 0 NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT venue_packages_hours_check CHECK ((hours > (0)::numeric)),
    CONSTRAINT venue_packages_original_price_check CHECK (((original_price IS NULL) OR (original_price >= (0)::numeric))),
    CONSTRAINT venue_packages_price_check CHECK ((price >= (0)::numeric)),
    CONSTRAINT venue_packages_validity_days_check CHECK ((validity_days > 0))
);



CREATE TABLE public.venue_receipts (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    session_id uuid,
    venue_id uuid NOT NULL,
    receipt_no text NOT NULL,
    items jsonb DEFAULT '[]'::jsonb NOT NULL,
    subtotal numeric(10,2),
    tax numeric(10,2),
    total numeric(10,2),
    payment_method text,
    issued_at timestamp with time zone DEFAULT now(),
    issued_by uuid
);



CREATE TABLE public.venue_reviews (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    user_id uuid NOT NULL,
    rating integer NOT NULL,
    comment text,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    CONSTRAINT venue_reviews_rating_check CHECK (((rating >= 1) AND (rating <= 5)))
);



CREATE TABLE public.venue_session_events (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    session_id uuid,
    event_type text NOT NULL,
    metadata jsonb DEFAULT '{}'::jsonb,
    created_at timestamp with time zone DEFAULT now(),
    CONSTRAINT venue_session_events_event_type_check CHECK ((event_type = ANY (ARRAY['started'::text, 'ended'::text, 'extended'::text, 'locked'::text, 'unlocked'::text, 'alert'::text, 'paused'::text, 'resumed'::text, 'billed'::text])))
);



CREATE TABLE public.venue_sessions (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    station_id text NOT NULL,
    user_id uuid,
    booking_id uuid,
    session_type text DEFAULT 'web_booking'::text NOT NULL,
    started_at timestamp with time zone DEFAULT now() NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    ended_at timestamp with time zone,
    total_charged numeric(10,2) DEFAULT 0,
    payment_method text,
    display_name text,
    created_at timestamp with time zone DEFAULT now(),
    notes text,
    zone text,
    package_id text,
    staff_id uuid,
    refund_amount numeric(10,2) DEFAULT 0,
    refund_method text,
    refund_reason text,
    refunded_at timestamp with time zone,
    refunded_by uuid,
    rate_per_hour numeric(10,2),
    member_id uuid,
    ended_by uuid,
    duration_minutes integer,
    CONSTRAINT venue_sessions_refund_method_check CHECK (((refund_method IS NULL) OR (refund_method = ANY (ARRAY['wallet'::text, 'cash'::text])))),
    CONSTRAINT venue_sessions_session_type_check CHECK ((session_type = ANY (ARRAY['web_booking'::text, 'hourly'::text, 'package'::text, 'voucher'::text, 'admin'::text, 'complimentary'::text])))
);



CREATE TABLE public.venue_staff (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    user_id uuid NOT NULL,
    role text NOT NULL,
    invited_by uuid,
    invited_at timestamp with time zone DEFAULT now(),
    accepted_at timestamp with time zone,
    status text DEFAULT 'active'::text NOT NULL,
    pin_code_hash text,
    CONSTRAINT venue_staff_role_check_v2 CHECK ((role = ANY (ARRAY['owner'::text, 'manager'::text, 'cashier'::text, 'technician'::text, 'staff'::text])))
);



CREATE TABLE public.venue_staff_invites (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    email text NOT NULL,
    role text NOT NULL,
    token text DEFAULT (gen_random_uuid())::text NOT NULL,
    expires_at timestamp with time zone DEFAULT (now() + '7 days'::interval) NOT NULL,
    used_at timestamp with time zone,
    invited_by uuid,
    CONSTRAINT venue_staff_invites_role_check CHECK ((role = ANY (ARRAY['manager'::text, 'cashier'::text])))
);



CREATE TABLE public.venue_station_status (
    station_id text NOT NULL,
    venue_id uuid NOT NULL,
    status text DEFAULT 'free'::text NOT NULL,
    session_type text,
    display_name text,
    expires_at timestamp with time zone,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT valid_status CHECK ((status = ANY (ARRAY['free'::text, 'occupied'::text, 'reserved'::text])))
);



CREATE TABLE public.venue_stations (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    station_id text NOT NULL,
    label text DEFAULT ''::text NOT NULL,
    zone text,
    pos_x numeric(5,2),
    pos_y numeric(5,2),
    created_at timestamp with time zone DEFAULT now(),
    status text DEFAULT 'active'::text NOT NULL,
    maintenance_note text,
    maintenance_since timestamp with time zone,
    width numeric(5,2) DEFAULT 1 NOT NULL,
    height numeric(5,2) DEFAULT 1 NOT NULL,
    rotation numeric(5,2) DEFAULT 0 NOT NULL,
    zone_id uuid,
    CONSTRAINT venue_stations_status_check CHECK ((status = ANY (ARRAY['active'::text, 'maintenance'::text, 'decommissioned'::text])))
);



CREATE TABLE public.verification_requests (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    user_id uuid NOT NULL,
    requested_role public.app_role NOT NULL,
    business_name text NOT NULL,
    business_type text NOT NULL,
    business_description text NOT NULL,
    experience_description text NOT NULL,
    first_name text NOT NULL,
    last_name text NOT NULL,
    email text NOT NULL,
    phone text,
    date_of_birth date NOT NULL,
    website_url text,
    social_media_links jsonb DEFAULT '{}'::jsonb,
    cnic_front_url text NOT NULL,
    cnic_back_url text NOT NULL,
    additional_documents jsonb DEFAULT '[]'::jsonb,
    status text DEFAULT 'pending'::text,
    reviewed_by uuid,
    reviewed_at timestamp with time zone,
    rejection_reason text,
    verification_notes text,
    admin_comments jsonb DEFAULT '[]'::jsonb,
    submitted_at timestamp with time zone DEFAULT now(),
    last_updated_at timestamp with time zone DEFAULT now(),
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    organizer_data jsonb,
    venue_data jsonb,
    venue_images jsonb,
    contact_email character varying(255),
    CONSTRAINT verification_requests_requested_role_check CHECK ((requested_role = ANY (ARRAY['organizer'::public.app_role, 'venue_owner'::public.app_role]))),
    CONSTRAINT verification_requests_status_check CHECK ((status = ANY (ARRAY['pending'::text, 'under_review'::text, 'approved'::text, 'rejected'::text])))
);



CREATE TABLE public.verified_roles (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    user_id uuid NOT NULL,
    role public.app_role NOT NULL,
    verified_at timestamp with time zone DEFAULT now(),
    verified_by uuid,
    verification_request_id uuid,
    expires_at timestamp with time zone,
    is_active boolean DEFAULT true,
    status character varying(20) DEFAULT 'approved'::character varying,
    reviewed_at timestamp with time zone,
    updated_at timestamp with time zone DEFAULT now(),
    CONSTRAINT verified_roles_role_check CHECK ((role = ANY (ARRAY['organizer'::public.app_role, 'venue_owner'::public.app_role]))),
    CONSTRAINT verified_roles_status_check CHECK (((status)::text = ANY (ARRAY[('pending'::character varying)::text, ('approved'::character varying)::text, ('rejected'::character varying)::text])))
);



CREATE TABLE public.walk_in_queue (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    member_id uuid,
    customer_name text,
    zone_id uuid,
    station_preference text,
    requested_duration_minutes integer DEFAULT 60,
    status text DEFAULT 'waiting'::text NOT NULL,
    assigned_station_id text,
    assigned_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    "position" integer DEFAULT 0 NOT NULL,
    CONSTRAINT walk_in_queue_status_check CHECK ((status = ANY (ARRAY['waiting'::text, 'assigned'::text, 'cancelled'::text, 'expired'::text])))
);



CREATE TABLE public.wallet_transactions (
    id uuid DEFAULT extensions.uuid_generate_v4() NOT NULL,
    wallet_id uuid NOT NULL,
    venue_id uuid NOT NULL,
    type text NOT NULL,
    amount numeric(12,2) NOT NULL,
    balance_after numeric(12,2) NOT NULL,
    description text,
    reference_id text,
    created_by uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT wallet_transactions_type_check CHECK ((type = ANY (ARRAY['topup'::text, 'deduct'::text, 'bonus'::text, 'refund'::text, 'redeem'::text])))
);



CREATE TABLE public.zones (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    venue_id uuid NOT NULL,
    name text NOT NULL,
    color text DEFAULT '#3b82f6'::text NOT NULL,
    hourly_rate numeric(10,2) DEFAULT 0 NOT NULL,
    sort_order integer DEFAULT 0 NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now()
);



ALTER TABLE ONLY public.schemaversions ALTER COLUMN schemaversionsid SET DEFAULT nextval('public.schemaversions_schemaversionsid_seq'::regclass);



ALTER TABLE ONLY public.schemaversions
    ADD CONSTRAINT "PK_schemaversions_Id" PRIMARY KEY (schemaversionsid);



ALTER TABLE ONLY public.account_security_state
    ADD CONSTRAINT account_security_state_pkey PRIMARY KEY (user_id);



ALTER TABLE ONLY public.activity_log
    ADD CONSTRAINT activity_log_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.admin_alerts
    ADD CONSTRAINT admin_alerts_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.admin_dashboard_preferences
    ADD CONSTRAINT admin_dashboard_preferences_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.admin_game_assignments
    ADD CONSTRAINT admin_game_assignments_admin_id_game_id_scope_key UNIQUE (admin_id, game_id, scope);



ALTER TABLE ONLY public.admin_game_assignments
    ADD CONSTRAINT admin_game_assignments_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.admin_impersonation_sessions
    ADD CONSTRAINT admin_impersonation_sessions_jti_key UNIQUE (jti);



ALTER TABLE ONLY public.admin_impersonation_sessions
    ADD CONSTRAINT admin_impersonation_sessions_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.admin_ip_allowlist
    ADD CONSTRAINT admin_ip_allowlist_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.admin_permissions
    ADD CONSTRAINT admin_permissions_name_key UNIQUE (name);



ALTER TABLE ONLY public.admin_permissions
    ADD CONSTRAINT admin_permissions_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.admin_role_permissions
    ADD CONSTRAINT admin_role_permissions_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.admin_role_permissions
    ADD CONSTRAINT admin_role_permissions_role_id_permission_id_key UNIQUE (role_id, permission_id);



ALTER TABLE ONLY public.admin_roles
    ADD CONSTRAINT admin_roles_key_key UNIQUE (key);



ALTER TABLE ONLY public.admin_roles
    ADD CONSTRAINT admin_roles_name_key UNIQUE (name);



ALTER TABLE ONLY public.admin_roles
    ADD CONSTRAINT admin_roles_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.admin_session_audit
    ADD CONSTRAINT admin_session_audit_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.admin_user_roles
    ADD CONSTRAINT admin_user_roles_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.admin_user_roles
    ADD CONSTRAINT admin_user_roles_user_id_role_id_key UNIQUE (user_id, role_id);



ALTER TABLE ONLY public.anomaly_events
    ADD CONSTRAINT anomaly_events_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.anomaly_rules
    ADD CONSTRAINT anomaly_rules_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.audit_logs
    ADD CONSTRAINT audit_logs_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.balance_transactions
    ADD CONSTRAINT balance_transactions_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.booking_rules
    ADD CONSTRAINT booking_rules_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.booking_rules
    ADD CONSTRAINT booking_rules_venue_id_key UNIQUE (venue_id);



ALTER TABLE ONLY public.br_game_data
    ADD CONSTRAINT br_game_data_pkey PRIMARY KEY (tournament_id);



ALTER TABLE ONLY public.br_games
    ADD CONSTRAINT br_games_lobby_id_game_number_key UNIQUE (lobby_id, game_number);



ALTER TABLE ONLY public.br_games
    ADD CONSTRAINT br_games_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.br_group_teams
    ADD CONSTRAINT br_group_teams_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.br_groups
    ADD CONSTRAINT br_groups_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.br_groups
    ADD CONSTRAINT br_groups_stage_id_group_order_key UNIQUE (stage_id, group_order);



ALTER TABLE ONLY public.br_lobbies
    ADD CONSTRAINT br_lobbies_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.br_lobbies
    ADD CONSTRAINT br_lobbies_stage_id_wave_number_lobby_index_key UNIQUE (stage_id, wave_number, lobby_index);



ALTER TABLE ONLY public.br_lobby_groups
    ADD CONSTRAINT br_lobby_groups_lobby_id_group_id_key UNIQUE (lobby_id, group_id);



ALTER TABLE ONLY public.br_lobby_groups
    ADD CONSTRAINT br_lobby_groups_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.br_lobby_readiness
    ADD CONSTRAINT br_lobby_readiness_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.br_lobby_evidence
    ADD CONSTRAINT br_round_evidence_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.br_lobby_results
    ADD CONSTRAINT br_round_results_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.brkt_advancements
    ADD CONSTRAINT brkt_advancements_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.brkt_advancements
    ADD CONSTRAINT brkt_advancements_source_match_id_type_key UNIQUE (source_match_id, type);



ALTER TABLE ONLY public.brkt_advancements
    ADD CONSTRAINT brkt_advancements_target_match_id_target_slot_key UNIQUE (target_match_id, target_slot);



ALTER TABLE ONLY public.brkt_layout
    ADD CONSTRAINT brkt_layout_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.brkt_layout
    ADD CONSTRAINT brkt_layout_version_id_match_id_key UNIQUE (version_id, match_id);



ALTER TABLE ONLY public.brkt_match_events
    ADD CONSTRAINT brkt_match_events_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.brkt_match_games
    ADD CONSTRAINT brkt_match_games_match_id_game_number_key UNIQUE (match_id, game_number);



ALTER TABLE ONLY public.brkt_match_games
    ADD CONSTRAINT brkt_match_games_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.brkt_matches
    ADD CONSTRAINT brkt_matches_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.brkt_matches
    ADD CONSTRAINT brkt_matches_version_id_bracket_type_round_index_match_numb_key UNIQUE (version_id, bracket_type, round_index, match_number);



ALTER TABLE ONLY public.brkt_versions
    ADD CONSTRAINT brkt_versions_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.brkt_versions
    ADD CONSTRAINT brkt_versions_tournament_id_version_number_key UNIQUE (tournament_id, version_number);



ALTER TABLE ONLY public.broadcast_deliveries
    ADD CONSTRAINT broadcast_deliveries_broadcast_id_user_id_channel_key UNIQUE (broadcast_id, user_id, channel);



ALTER TABLE ONLY public.broadcast_deliveries
    ADD CONSTRAINT broadcast_deliveries_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.broadcast_licenses
    ADD CONSTRAINT broadcast_licenses_license_key_key UNIQUE (license_key);



ALTER TABLE ONLY public.broadcast_licenses
    ADD CONSTRAINT broadcast_licenses_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.broadcast_overlay_layouts
    ADD CONSTRAINT broadcast_overlay_layouts_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.broadcast_templates
    ADD CONSTRAINT broadcast_templates_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.broadcast_themes
    ADD CONSTRAINT broadcast_themes_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.broadcasts
    ADD CONSTRAINT broadcasts_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.consent_records
    ADD CONSTRAINT consent_records_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.customer_wallets
    ADD CONSTRAINT customer_wallets_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.customer_wallets
    ADD CONSTRAINT customer_wallets_user_id_venue_id_key UNIQUE (user_id, venue_id);



ALTER TABLE ONLY public.daily_sponsor_stats
    ADD CONSTRAINT daily_sponsor_stats_pkey PRIMARY KEY (sponsor_id, stat_date);



ALTER TABLE ONLY public.daily_stats
    ADD CONSTRAINT daily_stats_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.daily_stats
    ADD CONSTRAINT daily_stats_venue_id_date_key UNIQUE (venue_id, date);



ALTER TABLE ONLY public.dispute_comments
    ADD CONSTRAINT dispute_comments_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.dispute_read_receipts
    ADD CONSTRAINT dispute_read_receipts_pkey PRIMARY KEY (dispute_id, user_id);



ALTER TABLE ONLY public.disputes
    ADD CONSTRAINT disputes_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.feature_flag_overrides
    ADD CONSTRAINT feature_flag_overrides_flag_id_user_id_key UNIQUE (flag_id, user_id);



ALTER TABLE ONLY public.feature_flag_overrides
    ADD CONSTRAINT feature_flag_overrides_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.feature_flag_rules
    ADD CONSTRAINT feature_flag_rules_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.feature_flags
    ADD CONSTRAINT feature_flags_key_key UNIQUE (key);



ALTER TABLE ONLY public.feature_flags
    ADD CONSTRAINT feature_flags_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.game_catalog_game_aliases
    ADD CONSTRAINT game_catalog_game_aliases_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.game_catalog_game_modes
    ADD CONSTRAINT game_catalog_game_modes_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.game_catalog_games
    ADD CONSTRAINT game_catalog_games_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.game_catalog_tournament_structures
    ADD CONSTRAINT game_catalog_tournament_structures_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.game_catalog_versions
    ADD CONSTRAINT game_catalog_versions_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.game_maps
    ADD CONSTRAINT game_maps_game_map_name_key UNIQUE (game, map_name);



ALTER TABLE ONLY public.game_maps
    ADD CONSTRAINT game_maps_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.game_servers
    ADD CONSTRAINT game_servers_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.games_metadata
    ADD CONSTRAINT games_metadata_pkey PRIMARY KEY (game_name);



ALTER TABLE ONLY public.gdpr_requests
    ADD CONSTRAINT gdpr_requests_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.ghost_approvals
    ADD CONSTRAINT ghost_approvals_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.ghost_data_access_log
    ADD CONSTRAINT ghost_data_access_log_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.ghost_sessions
    ADD CONSTRAINT ghost_sessions_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.leaderboard
    ADD CONSTRAINT leaderboard_pkey PRIMARY KEY (user_id, game);



ALTER TABLE ONLY public.licenses
    ADD CONSTRAINT licenses_license_id_key UNIQUE (license_id);



ALTER TABLE ONLY public.licenses
    ADD CONSTRAINT licenses_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.loyalty_accounts
    ADD CONSTRAINT loyalty_accounts_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.loyalty_accounts
    ADD CONSTRAINT loyalty_accounts_user_id_key UNIQUE (user_id);



ALTER TABLE ONLY public.loyalty_transactions
    ADD CONSTRAINT loyalty_transactions_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.match_checkins
    ADD CONSTRAINT match_checkins_match_id_team_id_key UNIQUE (match_id, team_id);



ALTER TABLE ONLY public.match_checkins
    ADD CONSTRAINT match_checkins_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.match_completed_events
    ADD CONSTRAINT match_completed_events_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.match_disputes
    ADD CONSTRAINT match_disputes_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.match_map_veto_actions
    ADD CONSTRAINT match_map_veto_actions_pkey1 PRIMARY KEY (id);



ALTER TABLE ONLY public.match_map_veto_actions
    ADD CONSTRAINT match_map_veto_actions_veto_id_action_number_key UNIQUE (veto_id, action_number);



ALTER TABLE ONLY public.match_map_vetos
    ADD CONSTRAINT match_map_vetos_match_id_unique UNIQUE (match_id);



ALTER TABLE ONLY public.match_map_vetos
    ADD CONSTRAINT match_map_vetos_pkey1 PRIMARY KEY (id);



ALTER TABLE ONLY public.match_messages
    ADD CONSTRAINT match_messages_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.match_player_stats
    ADD CONSTRAINT match_player_stats_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.match_result_reports
    ADD CONSTRAINT match_result_reports_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.match_time_proposals
    ADD CONSTRAINT match_time_proposals_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.member_packages
    ADD CONSTRAINT member_packages_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.members
    ADD CONSTRAINT members_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.moderation_queue
    ADD CONSTRAINT moderation_queue_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.notification_preferences
    ADD CONSTRAINT notification_preferences_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.notification_preferences
    ADD CONSTRAINT notification_preferences_venue_id_user_id_key UNIQUE (venue_id, user_id);



ALTER TABLE ONLY public.notifications
    ADD CONSTRAINT notifications_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.operations_audit_log
    ADD CONSTRAINT operations_audit_log_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.organization_albums
    ADD CONSTRAINT organization_albums_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.organization_media
    ADD CONSTRAINT organization_media_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.organization_staff
    ADD CONSTRAINT organization_staff_organization_id_user_id_key UNIQUE (organization_id, user_id);



ALTER TABLE ONLY public.organization_staff
    ADD CONSTRAINT organization_staff_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.organizations
    ADD CONSTRAINT organizations_owner_id_key UNIQUE (owner_id);



ALTER TABLE ONLY public.organizations
    ADD CONSTRAINT organizations_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.organizations
    ADD CONSTRAINT organizations_slug_key UNIQUE (slug);



ALTER TABLE ONLY public.partner_applications
    ADD CONSTRAINT partner_applications_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.partner_sponsor_invitations
    ADD CONSTRAINT partner_sponsor_invitations_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.partner_sponsor_invitations
    ADD CONSTRAINT partner_sponsor_invitations_token_hash_key UNIQUE (token_hash);



ALTER TABLE ONLY public.player_balance
    ADD CONSTRAINT player_balance_pkey PRIMARY KEY (user_id);



ALTER TABLE ONLY public.player_steam_accounts
    ADD CONSTRAINT player_steam_accounts_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.pos_orders
    ADD CONSTRAINT pos_orders_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.profiles
    ADD CONSTRAINT profiles_license_id_key UNIQUE (license_id);



ALTER TABLE ONLY public.profiles
    ADD CONSTRAINT profiles_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.profiles
    ADD CONSTRAINT profiles_slug_key UNIQUE (slug);



ALTER TABLE ONLY public.profiles
    ADD CONSTRAINT profiles_username_key UNIQUE (username);



ALTER TABLE ONLY public.public_tool_bracket_versions
    ADD CONSTRAINT public_tool_bracket_versions_bracket_id_version_number_key UNIQUE (bracket_id, version_number);



ALTER TABLE ONLY public.public_tool_bracket_versions
    ADD CONSTRAINT public_tool_bracket_versions_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.public_tool_brackets
    ADD CONSTRAINT public_tool_brackets_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.public_tool_brackets
    ADD CONSTRAINT public_tool_brackets_share_token_key UNIQUE (share_token);



ALTER TABLE ONLY public.public_veto_actions
    ADD CONSTRAINT public_veto_actions_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.public_veto_sessions
    ADD CONSTRAINT public_veto_sessions_host_token_key UNIQUE (host_token);



ALTER TABLE ONLY public.public_veto_sessions
    ADD CONSTRAINT public_veto_sessions_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.public_veto_sessions
    ADD CONSTRAINT public_veto_sessions_team1_token_key UNIQUE (team1_token);



ALTER TABLE ONLY public.public_veto_sessions
    ADD CONSTRAINT public_veto_sessions_team2_token_key UNIQUE (team2_token);



ALTER TABLE ONLY public.report_run_log
    ADD CONSTRAINT report_run_log_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.report_schedules
    ADD CONSTRAINT report_schedules_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.reviews
    ADD CONSTRAINT reviews_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.revoked_sessions
    ADD CONSTRAINT revoked_sessions_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.revoked_sessions
    ADD CONSTRAINT revoked_sessions_user_id_revoked_at_key UNIQUE (user_id, revoked_at);



ALTER TABLE ONLY public.riot_accounts
    ADD CONSTRAINT riot_accounts_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.riot_accounts
    ADD CONSTRAINT riot_accounts_puuid_key UNIQUE (puuid);



ALTER TABLE ONLY public.session_invoices
    ADD CONSTRAINT session_invoices_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.session_refunds
    ADD CONSTRAINT session_refunds_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.sponsor_accounts
    ADD CONSTRAINT sponsor_accounts_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.sponsor_accounts
    ADD CONSTRAINT sponsor_accounts_user_id_sponsor_id_key UNIQUE (user_id, sponsor_id);



ALTER TABLE ONLY public.sponsor_analytics_events
    ADD CONSTRAINT sponsor_analytics_events_pkey PRIMARY KEY (event_sequence);



ALTER TABLE ONLY public.sponsor_analytics_events
    ADD CONSTRAINT sponsor_analytics_events_sponsor_id_event_id_key UNIQUE (sponsor_id, event_id);



ALTER TABLE ONLY public.sponsor_analytics_exports
    ADD CONSTRAINT sponsor_analytics_exports_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.sponsor_audience_daily_facts
    ADD CONSTRAINT sponsor_audience_daily_facts_pkey PRIMARY KEY (sponsor_id, fact_date, audience_id, event_type);



ALTER TABLE ONLY public.sponsor_audience_identities
    ADD CONSTRAINT sponsor_audience_identities_pkey PRIMARY KEY (sponsor_id, identity_lookup);



ALTER TABLE ONLY public.sponsor_audience_identities
    ADD CONSTRAINT sponsor_audience_identities_sponsor_id_audience_id_key UNIQUE (sponsor_id, audience_id);



ALTER TABLE ONLY public.sponsor_content_daily_stats
    ADD CONSTRAINT sponsor_content_daily_stats_pk UNIQUE (sponsor_id, stat_date, tournament_id, page_path);



ALTER TABLE ONLY public.sponsor_daily_totals
    ADD CONSTRAINT sponsor_daily_totals_pkey PRIMARY KEY (sponsor_id, stat_date);



ALTER TABLE ONLY public.sponsor_device_daily_stats
    ADD CONSTRAINT sponsor_device_daily_stats_pkey PRIMARY KEY (sponsor_id, stat_date, device_class);



ALTER TABLE ONLY public.sponsor_impressions
    ADD CONSTRAINT sponsor_impressions_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.sponsor_placement_daily_stats
    ADD CONSTRAINT sponsor_placement_daily_stats_pkey PRIMARY KEY (sponsor_id, stat_date, placement);



ALTER TABLE ONLY public.sponsor_placements
    ADD CONSTRAINT sponsor_placements_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.sponsors
    ADD CONSTRAINT sponsors_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.staff_audit_log
    ADD CONSTRAINT staff_audit_log_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.staff_permissions
    ADD CONSTRAINT staff_permissions_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.staff_permissions
    ADD CONSTRAINT staff_permissions_venue_id_role_permission_key UNIQUE (venue_id, role, permission);



ALTER TABLE ONLY public.staff_shifts
    ADD CONSTRAINT staff_shifts_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.staff_tournament_assignments
    ADD CONSTRAINT staff_tournament_assignments_organization_staff_id_tourname_key UNIQUE (organization_staff_id, tournament_id);



ALTER TABLE ONLY public.staff_tournament_assignments
    ADD CONSTRAINT staff_tournament_assignments_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.stage_participants
    ADD CONSTRAINT stage_participants_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.station_health_snapshots
    ADD CONSTRAINT station_health_snapshots_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.system_config
    ADD CONSTRAINT system_config_pkey PRIMARY KEY (key);



ALTER TABLE ONLY public.system_settings
    ADD CONSTRAINT system_settings_pkey PRIMARY KEY (key);



ALTER TABLE ONLY public.team_invitations
    ADD CONSTRAINT team_invitations_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.team_members
    ADD CONSTRAINT team_members_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.team_members
    ADD CONSTRAINT team_members_team_id_user_id_key UNIQUE (team_id, user_id);



ALTER TABLE ONLY public.team_roster_members
    ADD CONSTRAINT team_roster_members_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.team_roster_members
    ADD CONSTRAINT team_roster_members_roster_id_user_id_key UNIQUE (roster_id, user_id);



ALTER TABLE ONLY public.team_rosters
    ADD CONSTRAINT team_rosters_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.teams
    ADD CONSTRAINT teams_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.teams
    ADD CONSTRAINT teams_tag_key UNIQUE (tag);



ALTER TABLE ONLY public.tournament_announcements
    ADD CONSTRAINT tournament_announcements_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.tournament_bans
    ADD CONSTRAINT tournament_bans_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.tournament_bans
    ADD CONSTRAINT tournament_bans_tournament_id_user_id_key UNIQUE (tournament_id, user_id);



ALTER TABLE ONLY public.tournament_disputes
    ADD CONSTRAINT tournament_disputes_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.tournament_disputes
    ADD CONSTRAINT tournament_disputes_reference_number_key UNIQUE (reference_number);



ALTER TABLE ONLY public.tournament_invitations
    ADD CONSTRAINT tournament_invitations_code_key UNIQUE (code);



ALTER TABLE ONLY public.tournament_invitations
    ADD CONSTRAINT tournament_invitations_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.tournament_map_pools
    ADD CONSTRAINT tournament_map_pools_pkey1 PRIMARY KEY (id);



ALTER TABLE ONLY public.tournament_map_pools
    ADD CONSTRAINT tournament_map_pools_tournament_id_map_id_key1 UNIQUE (tournament_id, map_id);



ALTER TABLE ONLY public.tournament_match_results
    ADD CONSTRAINT tournament_match_results_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.tournament_participants
    ADD CONSTRAINT tournament_participants_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.tournament_stages
    ADD CONSTRAINT tournament_stages_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.tournaments
    ADD CONSTRAINT tournaments_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.tournaments
    ADD CONSTRAINT tournaments_slug_key UNIQUE (slug);



ALTER TABLE ONLY public.riot_accounts
    ADD CONSTRAINT unique_user_riot UNIQUE (user_id);



ALTER TABLE ONLY public.admin_dashboard_preferences
    ADD CONSTRAINT uq_dashboard_prefs_user UNIQUE (user_id);



ALTER TABLE ONLY public.admin_ip_allowlist
    ADD CONSTRAINT uq_ip_allowlist_address UNIQUE (ip_address);



ALTER TABLE ONLY public.match_player_stats
    ADD CONSTRAINT uq_match_player_stats_match_map_player UNIQUE (match_id, map_number, steam64_id);



ALTER TABLE ONLY public.player_steam_accounts
    ADD CONSTRAINT uq_player_steam_accounts_steam64_id UNIQUE (steam64_id);



ALTER TABLE ONLY public.player_steam_accounts
    ADD CONSTRAINT uq_player_steam_accounts_user_id UNIQUE (user_id);



ALTER TABLE ONLY public.user_roles
    ADD CONSTRAINT user_roles_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.user_roles
    ADD CONSTRAINT user_roles_user_role_key UNIQUE (user_id, role);



ALTER TABLE ONLY public.game_catalog_games
    ADD CONSTRAINT ux_game_catalog_games_version_slug UNIQUE (version_id, slug);



ALTER TABLE ONLY public.game_catalog_game_modes
    ADD CONSTRAINT ux_game_catalog_modes_version_game_mode UNIQUE (version_id, game_slug, mode_key);



ALTER TABLE ONLY public.game_catalog_tournament_structures
    ADD CONSTRAINT ux_game_catalog_structures_version_game_structure UNIQUE (version_id, game_slug, structure_key);



ALTER TABLE ONLY public.venue_announcements
    ADD CONSTRAINT venue_announcements_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.venue_availability
    ADD CONSTRAINT venue_availability_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.venue_availability_snapshot
    ADD CONSTRAINT venue_availability_snapshot_pkey PRIMARY KEY (venue_id);



ALTER TABLE ONLY public.venue_availability
    ADD CONSTRAINT venue_availability_venue_id_date_start_time_end_time_key UNIQUE (venue_id, date, start_time, end_time);



ALTER TABLE ONLY public.venue_billing_config
    ADD CONSTRAINT venue_billing_config_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.venue_billing_config
    ADD CONSTRAINT venue_billing_config_venue_id_key UNIQUE (venue_id);



ALTER TABLE ONLY public.venue_bookings
    ADD CONSTRAINT venue_bookings_booking_code_key UNIQUE (booking_code);



ALTER TABLE ONLY public.venue_bookings
    ADD CONSTRAINT venue_bookings_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.venue_combos
    ADD CONSTRAINT venue_combos_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.venue_impressions
    ADD CONSTRAINT venue_impressions_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.venue_live_status
    ADD CONSTRAINT venue_live_status_pkey PRIMARY KEY (venue_id);



ALTER TABLE ONLY public.venue_loyalty_config
    ADD CONSTRAINT venue_loyalty_config_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.venue_loyalty_config
    ADD CONSTRAINT venue_loyalty_config_venue_id_key UNIQUE (venue_id);



ALTER TABLE ONLY public.venue_menu_items
    ADD CONSTRAINT venue_menu_items_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.venue_packages
    ADD CONSTRAINT venue_packages_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.venue_receipts
    ADD CONSTRAINT venue_receipts_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.venue_reviews
    ADD CONSTRAINT venue_reviews_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.venue_session_events
    ADD CONSTRAINT venue_session_events_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.venue_sessions
    ADD CONSTRAINT venue_sessions_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.venue_staff_invites
    ADD CONSTRAINT venue_staff_invites_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.venue_staff_invites
    ADD CONSTRAINT venue_staff_invites_token_key UNIQUE (token);



ALTER TABLE ONLY public.venue_staff
    ADD CONSTRAINT venue_staff_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.venue_staff
    ADD CONSTRAINT venue_staff_venue_id_user_id_key UNIQUE (venue_id, user_id);



ALTER TABLE ONLY public.venue_station_status
    ADD CONSTRAINT venue_station_status_pkey PRIMARY KEY (station_id);



ALTER TABLE ONLY public.venue_stations
    ADD CONSTRAINT venue_stations_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.venue_stations
    ADD CONSTRAINT venue_stations_venue_id_station_id_key UNIQUE (venue_id, station_id);



ALTER TABLE ONLY public.venues
    ADD CONSTRAINT venues_desktop_pairing_token_key UNIQUE (desktop_pairing_token);



ALTER TABLE ONLY public.venues
    ADD CONSTRAINT venues_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.venues
    ADD CONSTRAINT venues_slug_key UNIQUE (slug);



ALTER TABLE ONLY public.venues
    ADD CONSTRAINT venues_venue_id_key UNIQUE (venue_id);



ALTER TABLE ONLY public.verification_requests
    ADD CONSTRAINT verification_requests_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.verified_roles
    ADD CONSTRAINT verified_roles_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.verified_roles
    ADD CONSTRAINT verified_roles_user_role_key UNIQUE (user_id, role);



ALTER TABLE ONLY public.walk_in_queue
    ADD CONSTRAINT walk_in_queue_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.wallet_transactions
    ADD CONSTRAINT wallet_transactions_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.zones
    ADD CONSTRAINT zones_pkey PRIMARY KEY (id);



ALTER TABLE ONLY public.zones
    ADD CONSTRAINT zones_venue_id_name_key UNIQUE (venue_id, name);



CREATE INDEX idx_activity_log_venue_action ON public.activity_log USING btree (venue_id, action);



CREATE INDEX idx_activity_log_venue_created ON public.activity_log USING btree (venue_id, created_at DESC);



CREATE INDEX idx_admin_alerts_severity ON public.admin_alerts USING btree (severity);



CREATE INDEX idx_admin_alerts_status_created ON public.admin_alerts USING btree (status, created_at DESC);



CREATE INDEX idx_admin_alerts_type ON public.admin_alerts USING btree (type);



CREATE INDEX idx_admin_game_assignments_admin_scope ON public.admin_game_assignments USING btree (admin_id, scope, game_id, expires_at);



CREATE INDEX idx_admin_impersonation_sessions_active ON public.admin_impersonation_sessions USING btree (jti, expires_at) WHERE (revoked_at IS NULL);



CREATE INDEX idx_admin_permissions_category_sort ON public.admin_permissions USING btree (category, sort_order, name);



CREATE INDEX idx_admin_permissions_resource_action ON public.admin_permissions USING btree (resource, action);



CREATE INDEX idx_admin_session_audit_created ON public.admin_session_audit USING btree (created_at DESC);



CREATE INDEX idx_admin_session_audit_user ON public.admin_session_audit USING btree (user_id);



CREATE INDEX idx_admin_session_audit_user_recent ON public.admin_session_audit USING btree (user_id, created_at DESC);



CREATE INDEX idx_anomaly_events_is_resolved ON public.anomaly_events USING btree (is_resolved, detected_at DESC);



CREATE INDEX idx_anomaly_events_rule_id ON public.anomaly_events USING btree (rule_id, detected_at DESC);



CREATE INDEX idx_anomaly_rules_metric_active ON public.anomaly_rules USING btree (metric, is_active);



CREATE INDEX idx_audit_action ON public.staff_audit_log USING btree (action);



CREATE INDEX idx_audit_actor_id ON public.staff_audit_log USING btree (actor_id);



CREATE INDEX idx_audit_created ON public.staff_audit_log USING btree (created_at DESC);



CREATE INDEX idx_audit_logs_action_type ON public.audit_logs USING btree (action_type);



CREATE INDEX idx_audit_logs_action_type_created ON public.audit_logs USING btree (action_type, created_at DESC);



CREATE INDEX idx_audit_logs_created_at ON public.audit_logs USING btree (created_at DESC);



CREATE INDEX idx_audit_logs_created_at_admin ON public.audit_logs USING btree (created_at DESC) WHERE (admin_id IS NOT NULL);



CREATE INDEX idx_audit_logs_severity ON public.audit_logs USING btree (severity);



CREATE INDEX idx_audit_logs_target ON public.audit_logs USING btree (target_type, target_id, created_at DESC);



CREATE INDEX idx_audit_logs_target_type ON public.audit_logs USING btree (target_type);



CREATE INDEX idx_audit_org_id ON public.staff_audit_log USING btree (organization_id);



CREATE INDEX idx_balance_transactions_user ON public.balance_transactions USING btree (user_id, created_at DESC);



CREATE INDEX idx_booking_rules_venue ON public.booking_rules USING btree (venue_id);



CREATE INDEX idx_br_games_lobby_id ON public.br_games USING btree (lobby_id);



CREATE INDEX idx_br_games_lobby_status ON public.br_games USING btree (lobby_id, status);



CREATE INDEX idx_br_group_teams_participant_id ON public.br_group_teams USING btree (participant_id);



CREATE INDEX idx_br_group_teams_team_id ON public.br_group_teams USING btree (team_id);



CREATE INDEX idx_br_groups_stage_id ON public.br_groups USING btree (stage_id);



CREATE INDEX idx_br_lobbies_stage_id ON public.br_lobbies USING btree (stage_id);



CREATE INDEX idx_br_lobbies_stage_wave ON public.br_lobbies USING btree (stage_id, wave_number);



CREATE INDEX idx_br_lobby_groups_group_id ON public.br_lobby_groups USING btree (group_id);



CREATE INDEX idx_br_lobby_groups_lobby_id ON public.br_lobby_groups USING btree (lobby_id);



CREATE INDEX idx_br_lobby_readiness_lobby_id ON public.br_lobby_readiness USING btree (lobby_id);



CREATE INDEX idx_br_round_evidence_participant_id ON public.br_lobby_evidence USING btree (participant_id);



CREATE INDEX idx_br_round_results_participant_id ON public.br_lobby_results USING btree (participant_id);



CREATE INDEX idx_br_round_results_team_id ON public.br_lobby_results USING btree (team_id);



CREATE INDEX idx_brkt_advancements_source ON public.brkt_advancements USING btree (source_match_id);



CREATE INDEX idx_brkt_advancements_target ON public.brkt_advancements USING btree (target_match_id);



CREATE INDEX idx_brkt_advancements_version ON public.brkt_advancements USING btree (version_id);



CREATE INDEX idx_brkt_events_match ON public.brkt_match_events USING btree (match_id);



CREATE INDEX idx_brkt_match_games_match ON public.brkt_match_games USING btree (match_id);



CREATE INDEX idx_brkt_match_games_riot_id ON public.brkt_match_games USING btree (riot_match_id);



CREATE INDEX idx_brkt_match_games_status ON public.brkt_match_games USING btree (verification_status);



CREATE INDEX idx_brkt_matches_checkin_reminder ON public.brkt_matches USING btree (check_in_reminder_sent, status, scheduled_time) WHERE ((check_in_reminder_sent = false) AND (status = 'scheduled'::text));



CREATE INDEX idx_brkt_matches_party_code ON public.brkt_matches USING btree (party_code) WHERE (party_code IS NOT NULL);



CREATE INDEX idx_brkt_matches_version ON public.brkt_matches USING btree (version_id);



CREATE INDEX idx_brkt_matches_version_status ON public.brkt_matches USING btree (version_id, status) WHERE (team1_id IS NOT NULL);



CREATE INDEX idx_broadcast_deliveries_broadcast ON public.broadcast_deliveries USING btree (broadcast_id);



CREATE INDEX idx_broadcast_deliveries_pending ON public.broadcast_deliveries USING btree (broadcast_id) WHERE (status = 'pending'::text);



CREATE INDEX idx_broadcast_deliveries_user ON public.broadcast_deliveries USING btree (user_id, status);



CREATE INDEX idx_broadcast_licenses_license_key ON public.broadcast_licenses USING btree (license_key);



CREATE INDEX idx_broadcast_licenses_status ON public.broadcast_licenses USING btree (status);



CREATE INDEX idx_broadcast_licenses_user_id ON public.broadcast_licenses USING btree (user_id);



CREATE INDEX idx_broadcast_overlay_layouts_game ON public.broadcast_overlay_layouts USING btree (game);



CREATE INDEX idx_broadcast_overlay_layouts_is_public ON public.broadcast_overlay_layouts USING btree (is_public) WHERE (is_public = true);



CREATE INDEX idx_broadcast_overlay_layouts_is_template ON public.broadcast_overlay_layouts USING btree (is_template) WHERE (is_template = true);



CREATE INDEX idx_broadcast_overlay_layouts_user_id ON public.broadcast_overlay_layouts USING btree (user_id);



CREATE INDEX idx_broadcasts_created ON public.broadcasts USING btree (created_at DESC);



CREATE INDEX idx_broadcasts_scheduled ON public.broadcasts USING btree (scheduled_at) WHERE (status = 'scheduled'::text);



CREATE INDEX idx_broadcasts_status ON public.broadcasts USING btree (status);



CREATE INDEX idx_consent_records_recorded_at ON public.consent_records USING btree (recorded_at DESC);



CREATE INDEX idx_consent_records_type_granted ON public.consent_records USING btree (consent_type, granted);



CREATE INDEX idx_consent_records_user_id ON public.consent_records USING btree (user_id);



CREATE INDEX idx_customer_wallets_user ON public.customer_wallets USING btree (user_id);



CREATE INDEX idx_customer_wallets_venue ON public.customer_wallets USING btree (venue_id);



CREATE INDEX idx_daily_sponsor_stats_sponsor_date ON public.daily_sponsor_stats USING btree (sponsor_id, stat_date);



CREATE INDEX idx_daily_stats_venue_date ON public.daily_stats USING btree (venue_id, date DESC);



CREATE INDEX idx_dc_attachment_url ON public.dispute_comments USING btree (attachment_url) WHERE (attachment_url IS NOT NULL);



CREATE INDEX idx_dc_created ON public.dispute_comments USING btree (created_at);



CREATE INDEX idx_dc_dispute ON public.dispute_comments USING btree (dispute_id);



CREATE INDEX idx_dc_user ON public.dispute_comments USING btree (user_id);



CREATE INDEX idx_dispute_read_receipts_user_id ON public.dispute_read_receipts USING btree (user_id);



CREATE INDEX idx_disputes_created ON public.disputes USING btree (created_at DESC);



CREATE INDEX idx_disputes_priority ON public.disputes USING btree (priority);



CREATE INDEX idx_disputes_status ON public.disputes USING btree (status);



CREATE INDEX idx_disputes_tournament ON public.disputes USING btree (tournament_id);



CREATE INDEX idx_feature_flag_overrides_flag ON public.feature_flag_overrides USING btree (flag_id);



CREATE INDEX idx_feature_flag_overrides_user ON public.feature_flag_overrides USING btree (user_id);



CREATE INDEX idx_feature_flag_rules_flag ON public.feature_flag_rules USING btree (flag_id);



CREATE INDEX idx_feature_flag_rules_priority ON public.feature_flag_rules USING btree (flag_id, priority DESC);



CREATE INDEX idx_feature_flags_enabled ON public.feature_flags USING btree (is_enabled) WHERE (is_enabled = true);



CREATE INDEX idx_feature_flags_key ON public.feature_flags USING btree (key);



CREATE INDEX idx_game_catalog_aliases_game_slug ON public.game_catalog_game_aliases USING btree (game_slug);



CREATE INDEX idx_game_catalog_games_banner_url ON public.game_catalog_games USING btree (slug) WHERE (banner_url IS NOT NULL);



CREATE INDEX idx_game_catalog_games_slug ON public.game_catalog_games USING btree (slug);



CREATE INDEX idx_game_catalog_modes_game_slug ON public.game_catalog_game_modes USING btree (game_slug);



CREATE INDEX idx_game_catalog_structures_game_slug ON public.game_catalog_tournament_structures USING btree (game_slug);



CREATE INDEX idx_game_maps_game ON public.game_maps USING btree (game) WHERE (is_active = true);



CREATE UNIQUE INDEX idx_game_servers_match_active ON public.game_servers USING btree (match_id) WHERE (deleted_at IS NULL);



CREATE INDEX idx_game_servers_match_id ON public.game_servers USING btree (match_id);



CREATE INDEX idx_game_servers_provider ON public.game_servers USING btree (provider);



CREATE INDEX idx_game_servers_status ON public.game_servers USING btree (status);



CREATE INDEX idx_gdpr_requests_requested_at ON public.gdpr_requests USING btree (requested_at DESC);



CREATE INDEX idx_gdpr_requests_status_type ON public.gdpr_requests USING btree (status, request_type);



CREATE INDEX idx_gdpr_requests_user_id ON public.gdpr_requests USING btree (user_id);



CREATE INDEX idx_ghost_approvals_expires ON public.ghost_approvals USING btree (expires_at) WHERE (status = 'approved'::text);



CREATE INDEX idx_ghost_approvals_requester ON public.ghost_approvals USING btree (requester_id);



CREATE INDEX idx_ghost_approvals_status ON public.ghost_approvals USING btree (status) WHERE (status = 'pending'::text);



CREATE INDEX idx_ghost_approvals_target ON public.ghost_approvals USING btree (target_user_id);



CREATE INDEX idx_ghost_data_access_session ON public.ghost_data_access_log USING btree (session_id);



CREATE INDEX idx_ghost_data_access_time ON public.ghost_data_access_log USING btree (accessed_at DESC);



CREATE INDEX idx_ghost_sessions_active ON public.ghost_sessions USING btree (admin_id) WHERE (ended_at IS NULL);



CREATE INDEX idx_ghost_sessions_admin ON public.ghost_sessions USING btree (admin_id);



CREATE INDEX idx_ghost_sessions_expires ON public.ghost_sessions USING btree (expires_at) WHERE (ended_at IS NULL);



CREATE INDEX idx_ghost_sessions_target ON public.ghost_sessions USING btree (target_user_id);



CREATE INDEX idx_health_snapshots_station ON public.station_health_snapshots USING btree (station_id, recorded_at DESC);



CREATE INDEX idx_health_snapshots_venue ON public.station_health_snapshots USING btree (venue_id, recorded_at DESC);



CREATE INDEX idx_invitations_expiry_job ON public.tournament_invitations USING btree (expiry_job_id) WHERE (expiry_job_id IS NOT NULL);



CREATE INDEX idx_ip_allowlist_active_address ON public.admin_ip_allowlist USING btree (is_active, ip_address) WHERE (is_active = true);



CREATE INDEX idx_loyalty_accounts_user ON public.loyalty_accounts USING btree (user_id);



CREATE INDEX idx_loyalty_txn_account ON public.loyalty_transactions USING btree (account_id);



CREATE INDEX idx_loyalty_txn_created_desc ON public.loyalty_transactions USING btree (created_at DESC);



CREATE INDEX idx_loyalty_txn_venue ON public.loyalty_transactions USING btree (venue_id);



CREATE INDEX idx_match_map_veto_actions_match_id ON public.match_map_veto_actions USING btree (match_id);



CREATE INDEX idx_match_map_veto_actions_veto_id ON public.match_map_veto_actions USING btree (veto_id);



CREATE INDEX idx_match_player_stats_match_id ON public.match_player_stats USING btree (match_id);



CREATE INDEX idx_match_player_stats_steam64_id ON public.match_player_stats USING btree (steam64_id);



CREATE INDEX idx_match_player_stats_user_id ON public.match_player_stats USING btree (user_id);



CREATE INDEX idx_match_result_reports_match ON public.match_result_reports USING btree (match_id);



CREATE UNIQUE INDEX idx_match_result_reports_match_game_reporter ON public.match_result_reports USING btree (match_id, game_number, reported_by_team_id);



CREATE INDEX idx_match_result_reports_status ON public.match_result_reports USING btree (match_id, status);



CREATE INDEX idx_member_packages_expires_at ON public.member_packages USING btree (expires_at) WHERE (status = 'active'::text);



CREATE INDEX idx_member_packages_status ON public.member_packages USING btree (venue_id, member_id, status) WHERE (status = 'active'::text);



CREATE INDEX idx_member_packages_venue_member ON public.member_packages USING btree (venue_id, member_id);



CREATE INDEX idx_members_search ON public.members USING btree (venue_id, display_name, email);



CREATE UNIQUE INDEX idx_members_user_venue ON public.members USING btree (user_id, venue_id) WHERE (user_id IS NOT NULL);



CREATE INDEX idx_members_venue ON public.members USING btree (venue_id);



CREATE INDEX idx_moderation_queue_content ON public.moderation_queue USING btree (content_type, content_id);



CREATE INDEX idx_moderation_queue_reporter ON public.moderation_queue USING btree (reported_by) WHERE (reported_by IS NOT NULL);



CREATE INDEX idx_moderation_queue_status_created ON public.moderation_queue USING btree (status, created_at DESC);



CREATE UNIQUE INDEX idx_mrr_match_game_team ON public.match_result_reports USING btree (match_id, game_number, reported_by_team_id);



CREATE INDEX idx_notification_preferences_user_id ON public.notification_preferences USING btree (user_id);



CREATE INDEX idx_notifications_created_at ON public.notifications USING btree (created_at);



CREATE INDEX idx_notifications_is_read ON public.notifications USING btree (is_read);



CREATE INDEX idx_notifications_user_id ON public.notifications USING btree (user_id);



CREATE INDEX idx_operations_audit_admin ON public.operations_audit_log USING btree (admin_id, created_at DESC);



CREATE INDEX idx_operations_audit_target ON public.operations_audit_log USING btree (resource_type, target_id, created_at DESC);



CREATE INDEX idx_org_staff_org_id ON public.organization_staff USING btree (organization_id);



CREATE INDEX idx_org_staff_status ON public.organization_staff USING btree (status);



CREATE INDEX idx_org_staff_user_id ON public.organization_staff USING btree (user_id);



CREATE INDEX idx_organization_staff_role ON public.organization_staff USING btree (role);



CREATE INDEX idx_organizations_owner ON public.organizations USING btree (owner_id);



CREATE INDEX idx_organizations_slug ON public.organizations USING btree (slug);



CREATE INDEX idx_partner_applications_created ON public.partner_applications USING btree (created_at DESC);



CREATE INDEX idx_partner_applications_status ON public.partner_applications USING btree (status);



CREATE INDEX idx_player_steam_accounts_steam64_id ON public.player_steam_accounts USING btree (steam64_id);



CREATE INDEX idx_pos_orders_created_at ON public.pos_orders USING btree (created_at DESC);



CREATE INDEX idx_pos_orders_kitchen ON public.pos_orders USING btree (venue_id, created_at) WHERE (status = ANY (ARRAY['pending'::text, 'preparing'::text]));



CREATE INDEX idx_pos_orders_venue_session ON public.pos_orders USING btree (venue_id, session_id);



CREATE INDEX idx_pos_orders_venue_status ON public.pos_orders USING btree (venue_id, status);



CREATE INDEX idx_profiles_email ON public.profiles USING btree (email);



CREATE INDEX idx_profiles_is_admin ON public.profiles USING btree (is_admin);



CREATE INDEX idx_profiles_is_verified ON public.profiles USING btree (is_verified);



CREATE UNIQUE INDEX idx_profiles_riot_tag_unique ON public.profiles USING btree (riot_tag) WHERE ((riot_tag IS NOT NULL) AND (riot_tag <> ''::text));



CREATE INDEX idx_profiles_role ON public.profiles USING btree (role);



CREATE INDEX idx_profiles_slug ON public.profiles USING btree (slug);



CREATE INDEX idx_profiles_username ON public.profiles USING btree (username);



CREATE INDEX idx_profiles_verification_status ON public.profiles USING btree (verification_status);



CREATE INDEX idx_public_tool_brackets_owner ON public.public_tool_brackets USING btree (owner_user_id, updated_at DESC);



CREATE INDEX idx_public_tool_brackets_share_token ON public.public_tool_brackets USING btree (share_token) WHERE (share_token IS NOT NULL);



CREATE INDEX idx_public_veto_actions_session ON public.public_veto_actions USING btree (session_id, action_number);



CREATE INDEX idx_public_veto_sessions_expires_at ON public.public_veto_sessions USING btree (expires_at);



CREATE INDEX idx_public_veto_sessions_tokens ON public.public_veto_sessions USING btree (host_token, team1_token, team2_token);



CREATE INDEX idx_report_run_log_schedule_started ON public.report_run_log USING btree (schedule_id, started_at DESC);



CREATE INDEX idx_report_schedules_active_next ON public.report_schedules USING btree (is_active, next_run_at) WHERE (is_active = true);



CREATE INDEX idx_revoked_sessions_expires ON public.revoked_sessions USING btree (expires_at);



CREATE INDEX idx_revoked_sessions_user_expires ON public.revoked_sessions USING btree (user_id, expires_at DESC);



CREATE INDEX idx_riot_accounts_puuid ON public.riot_accounts USING btree (puuid);



CREATE INDEX idx_riot_accounts_user_id ON public.riot_accounts USING btree (user_id);



CREATE INDEX idx_session_invoices_member ON public.session_invoices USING btree (member_id) WHERE (member_id IS NOT NULL);



CREATE INDEX idx_session_invoices_session ON public.session_invoices USING btree (session_id) WHERE (session_id IS NOT NULL);



CREATE INDEX idx_session_invoices_venue ON public.session_invoices USING btree (venue_id, created_at DESC);



CREATE INDEX idx_session_refunds_session ON public.session_refunds USING btree (session_id);



CREATE INDEX idx_session_refunds_venue ON public.session_refunds USING btree (venue_id);



CREATE INDEX idx_sponsor_impressions_event_type ON public.sponsor_impressions USING btree (event_type);



CREATE INDEX idx_sponsor_impressions_lookup ON public.sponsor_impressions USING btree (sponsor_id, event_type, created_at);



CREATE INDEX idx_sponsor_impressions_sponsor_id ON public.sponsor_impressions USING btree (sponsor_id);



CREATE INDEX idx_sponsor_impressions_tournament ON public.sponsor_impressions USING btree (tournament_id) WHERE (tournament_id IS NOT NULL);



CREATE INDEX idx_sponsor_impressions_visitor_id ON public.sponsor_impressions USING btree (visitor_id, created_at);



CREATE INDEX idx_sponsors_active ON public.sponsors USING btree (is_active) WHERE (is_active = true);



CREATE INDEX idx_sta_staff_id ON public.staff_tournament_assignments USING btree (organization_staff_id);



CREATE INDEX idx_sta_tourn_id ON public.staff_tournament_assignments USING btree (tournament_id);



CREATE INDEX idx_staff_permissions_venue_role ON public.staff_permissions USING btree (venue_id, role);



CREATE INDEX idx_staff_shifts_active ON public.staff_shifts USING btree (venue_id) WHERE (clocked_out IS NULL);



CREATE INDEX idx_staff_shifts_clocked_in ON public.staff_shifts USING btree (venue_id, clocked_in DESC);



CREATE INDEX idx_staff_shifts_staff ON public.staff_shifts USING btree (staff_id);



CREATE INDEX idx_staff_shifts_venue ON public.staff_shifts USING btree (venue_id);



CREATE INDEX idx_td_dispute_reason ON public.tournament_disputes USING btree (dispute_reason) WHERE (dispute_reason IS NOT NULL);



CREATE INDEX idx_td_raised_by ON public.tournament_disputes USING btree (raised_by_user_id);



CREATE INDEX idx_td_tournament ON public.tournament_disputes USING btree (tournament_id);



CREATE INDEX idx_team_members_active ON public.team_members USING btree (team_id, is_active);



CREATE INDEX idx_team_members_team_id ON public.team_members USING btree (team_id);



CREATE UNIQUE INDEX idx_team_members_unique_active ON public.team_members USING btree (team_id, user_id) WHERE (is_active = true);



CREATE INDEX idx_team_members_user_id ON public.team_members USING btree (user_id);



CREATE INDEX idx_team_rosters_game ON public.team_rosters USING btree (game);



CREATE UNIQUE INDEX idx_team_rosters_team_game_unique ON public.team_rosters USING btree (team_id, game);



CREATE INDEX idx_team_rosters_team_id ON public.team_rosters USING btree (team_id);



CREATE INDEX idx_teams_deleted_at ON public.teams USING btree (deleted_at) WHERE (deleted_at IS NOT NULL);



CREATE INDEX idx_teams_game ON public.teams USING btree (game);



CREATE INDEX idx_teams_is_active ON public.teams USING btree (is_active);



CREATE INDEX idx_teams_is_mock ON public.teams USING btree (is_mock);



CREATE INDEX idx_teams_owner_id ON public.teams USING btree (owner_id);



CREATE INDEX idx_teams_tag ON public.teams USING btree (tag);



CREATE INDEX idx_teams_team_kind ON public.teams USING btree (team_kind);



CREATE INDEX idx_tmr_reporter ON public.tournament_match_results USING btree (reporter_user_id);



CREATE INDEX idx_tmr_tournament ON public.tournament_match_results USING btree (tournament_id);



CREATE INDEX idx_tournament_announcements_created_at ON public.tournament_announcements USING btree (created_at);



CREATE INDEX idx_tournament_announcements_tournament_id ON public.tournament_announcements USING btree (tournament_id);



CREATE INDEX idx_tournament_bans_active ON public.tournament_bans USING btree (is_active) WHERE (is_active = true);



CREATE INDEX idx_tournament_bans_team ON public.tournament_bans USING btree (team_id) WHERE (team_id IS NOT NULL);



CREATE INDEX idx_tournament_bans_tournament_id ON public.tournament_bans USING btree (tournament_id);



CREATE INDEX idx_tournament_bans_user ON public.tournament_bans USING btree (user_id) WHERE (user_id IS NOT NULL);



CREATE INDEX idx_tournament_bans_user_id ON public.tournament_bans USING btree (user_id);



CREATE INDEX idx_tournament_invitations_code_lower ON public.tournament_invitations USING btree (lower(code));



CREATE INDEX idx_tournament_invitations_email_lower ON public.tournament_invitations USING btree (lower(email));



CREATE INDEX idx_tournament_invitations_redeemed_participant_id ON public.tournament_invitations USING btree (redeemed_participant_id) WHERE (redeemed_participant_id IS NOT NULL);



CREATE INDEX idx_tournament_invitations_status_expires ON public.tournament_invitations USING btree (status, expires_at);



CREATE INDEX idx_tournament_invitations_tournament_id ON public.tournament_invitations USING btree (tournament_id);



CREATE INDEX idx_tournament_participants_checkin ON public.tournament_participants USING btree (tournament_id, status, checked_in_at);



CREATE INDEX idx_tournament_participants_qualified ON public.tournament_participants USING btree (tournament_id, qualified_to_playoff);



CREATE INDEX idx_tournament_participants_registration_date ON public.tournament_participants USING btree (registration_date);



CREATE INDEX idx_tournament_participants_roster_id ON public.tournament_participants USING btree (roster_id) WHERE (roster_id IS NOT NULL);



CREATE INDEX idx_tournament_participants_source ON public.tournament_participants USING btree (tournament_id, source);



CREATE INDEX idx_tournament_participants_status ON public.tournament_participants USING btree (status);



CREATE INDEX idx_tournament_participants_team_captain_id ON public.tournament_participants USING btree (team_captain_id);



CREATE INDEX idx_tournament_participants_team_id ON public.tournament_participants USING btree (team_id);



CREATE INDEX idx_tournament_participants_tournament_id ON public.tournament_participants USING btree (tournament_id);



CREATE INDEX idx_tournament_participants_tournament_team ON public.tournament_participants USING btree (tournament_id, team_id) WHERE (status <> ALL (ARRAY['rejected'::public.registration_status, 'cancelled'::public.registration_status]));



CREATE INDEX idx_tournament_participants_type ON public.tournament_participants USING btree (participant_type);



CREATE INDEX idx_tournament_participants_user_id ON public.tournament_participants USING btree (user_id);



CREATE INDEX idx_tournament_stages_tournament_id ON public.tournament_stages USING btree (tournament_id);



CREATE INDEX idx_tournaments_checkin_reminder ON public.tournaments USING btree (check_in_reminder_sent, check_in_required, start_date) WHERE ((check_in_reminder_sent = false) AND (check_in_required = true));



CREATE INDEX idx_tournaments_created_via_season ON public.tournaments USING btree (created_via, season_id) WHERE (created_via = 'season'::text);



CREATE INDEX idx_tournaments_deleted_at ON public.tournaments USING btree (deleted_at) WHERE (deleted_at IS NOT NULL);



CREATE INDEX idx_tournaments_game ON public.tournaments USING btree (game);



CREATE INDEX idx_tournaments_game_mode ON public.tournaments USING btree (game_mode);



CREATE INDEX idx_tournaments_is_public ON public.tournaments USING btree (is_public);



CREATE INDEX idx_tournaments_organization ON public.tournaments USING btree (organization_id);



CREATE INDEX idx_tournaments_organizer_id ON public.tournaments USING btree (organizer_id);



CREATE INDEX idx_tournaments_season ON public.tournaments USING btree (season_id);



CREATE INDEX idx_tournaments_season_id ON public.tournaments USING btree (season_id) WHERE (season_id IS NOT NULL);



CREATE INDEX idx_tournaments_season_role ON public.tournaments USING btree (season_id, season_role);



CREATE INDEX idx_tournaments_slug ON public.tournaments USING btree (slug);



CREATE INDEX idx_tournaments_start_date ON public.tournaments USING btree (start_date);



CREATE INDEX idx_tournaments_status ON public.tournaments USING btree (status);



CREATE INDEX idx_tournaments_venue_id ON public.tournaments USING btree (venue_id);



CREATE INDEX idx_tp_mock_tournament ON public.tournament_participants USING btree (tournament_id) WHERE (is_mock = true);



CREATE INDEX idx_user_roles_active ON public.user_roles USING btree (user_id, is_active);



CREATE INDEX idx_user_roles_primary ON public.user_roles USING btree (user_id, is_primary) WHERE (is_primary = true);



CREATE INDEX idx_user_roles_role ON public.user_roles USING btree (role);



CREATE INDEX idx_user_roles_user_id ON public.user_roles USING btree (user_id);



CREATE INDEX idx_user_roles_user_role ON public.user_roles USING btree (user_id, role);



CREATE INDEX idx_venue_announcements_active ON public.venue_announcements USING btree (is_active);



CREATE INDEX idx_venue_announcements_expires ON public.venue_announcements USING btree (expires_at);



CREATE INDEX idx_venue_announcements_starts ON public.venue_announcements USING btree (starts_at);



CREATE INDEX idx_venue_announcements_venue ON public.venue_announcements USING btree (venue_id);



CREATE INDEX idx_venue_announcements_venue_active ON public.venue_announcements USING btree (venue_id, priority DESC) WHERE (is_active = true);



CREATE INDEX idx_venue_availability_venue_date ON public.venue_availability USING btree (venue_id, date);



CREATE INDEX idx_venue_bookings_booking_code ON public.venue_bookings USING btree (booking_code) WHERE (code_used_at IS NULL);



CREATE INDEX idx_venue_bookings_date ON public.venue_bookings USING btree (booking_date);



CREATE INDEX idx_venue_bookings_user_id ON public.venue_bookings USING btree (user_id);



CREATE INDEX idx_venue_bookings_venue_id ON public.venue_bookings USING btree (venue_id);



CREATE INDEX idx_venue_combos_active ON public.venue_combos USING btree (is_active);



CREATE INDEX idx_venue_combos_items_gin ON public.venue_combos USING gin (items);



CREATE INDEX idx_venue_combos_venue ON public.venue_combos USING btree (venue_id);



CREATE INDEX idx_venue_combos_venue_active ON public.venue_combos USING btree (venue_id, sort_order) WHERE (is_active = true);



CREATE INDEX idx_venue_impressions_venue_id ON public.venue_impressions USING btree (venue_id, event_type, created_at DESC);



CREATE INDEX idx_venue_loyalty_config_venue ON public.venue_loyalty_config USING btree (venue_id);



CREATE INDEX idx_venue_menu_items_available ON public.venue_menu_items USING btree (is_available);



CREATE INDEX idx_venue_menu_items_category ON public.venue_menu_items USING btree (category);



CREATE INDEX idx_venue_menu_items_venue ON public.venue_menu_items USING btree (venue_id);



CREATE INDEX idx_venue_menu_items_venue_avail ON public.venue_menu_items USING btree (venue_id, sort_order) WHERE (is_available = true);



CREATE INDEX idx_venue_packages_venue_active ON public.venue_packages USING btree (venue_id, is_active) WHERE (is_active = true);



CREATE INDEX idx_venue_packages_venue_id ON public.venue_packages USING btree (venue_id);



CREATE INDEX idx_venue_receipts_session ON public.venue_receipts USING btree (session_id);



CREATE INDEX idx_venue_receipts_venue ON public.venue_receipts USING btree (venue_id, issued_at DESC);



CREATE INDEX idx_venue_session_events_session ON public.venue_session_events USING btree (session_id, created_at DESC);



CREATE INDEX idx_venue_sessions_active ON public.venue_sessions USING btree (station_id) WHERE (ended_at IS NULL);



CREATE INDEX idx_venue_sessions_member ON public.venue_sessions USING btree (member_id) WHERE (member_id IS NOT NULL);



CREATE UNIQUE INDEX idx_venue_sessions_one_active_per_station ON public.venue_sessions USING btree (station_id, venue_id) WHERE (ended_at IS NULL);



CREATE INDEX idx_venue_sessions_refunded ON public.venue_sessions USING btree (venue_id) WHERE (refunded_at IS NOT NULL);



CREATE INDEX idx_venue_sessions_venue ON public.venue_sessions USING btree (venue_id, started_at DESC);



CREATE INDEX idx_venue_staff_status ON public.venue_staff USING btree (venue_id, status);



CREATE INDEX idx_venue_staff_user ON public.venue_staff USING btree (user_id);



CREATE INDEX idx_venue_staff_venue ON public.venue_staff USING btree (venue_id);



CREATE INDEX idx_venue_station_status_venue ON public.venue_station_status USING btree (venue_id);



CREATE INDEX idx_venue_stations_zone ON public.venue_stations USING btree (zone_id);



CREATE INDEX idx_venues_city ON public.venues USING btree (city);



CREATE INDEX idx_venues_deleted_at ON public.venues USING btree (deleted_at) WHERE (deleted_at IS NOT NULL);



CREATE INDEX idx_venues_is_active ON public.venues USING btree (is_active);



CREATE INDEX idx_venues_location ON public.venues USING btree (latitude, longitude);



CREATE INDEX idx_venues_organization ON public.venues USING btree (organization_id) WHERE (organization_id IS NOT NULL);



CREATE INDEX idx_verification_requests_organizer_data ON public.verification_requests USING gin (organizer_data);



CREATE INDEX idx_verification_requests_requested_role ON public.verification_requests USING btree (requested_role);



CREATE INDEX idx_verification_requests_status ON public.verification_requests USING btree (status);



CREATE INDEX idx_verification_requests_submitted_at ON public.verification_requests USING btree (submitted_at);



CREATE UNIQUE INDEX idx_verification_requests_unique_pending ON public.verification_requests USING btree (user_id, requested_role) WHERE (status = 'pending'::text);



CREATE INDEX idx_verification_requests_user_id ON public.verification_requests USING btree (user_id);



CREATE INDEX idx_verification_requests_venue_data ON public.verification_requests USING gin (venue_data);



CREATE INDEX idx_verified_roles_active ON public.verified_roles USING btree (user_id, role) WHERE (is_active = true);



CREATE INDEX idx_verified_roles_role ON public.verified_roles USING btree (role);



CREATE INDEX idx_verified_roles_status ON public.verified_roles USING btree (user_id, status);



CREATE UNIQUE INDEX idx_verified_roles_unique_active ON public.verified_roles USING btree (user_id, role) WHERE (is_active = true);



CREATE INDEX idx_verified_roles_user_id ON public.verified_roles USING btree (user_id);



CREATE INDEX idx_verified_roles_user_role ON public.verified_roles USING btree (user_id, role);



CREATE INDEX idx_walk_in_queue_venue_position ON public.walk_in_queue USING btree (venue_id, "position") WHERE (status = 'waiting'::text);



CREATE INDEX idx_walk_in_queue_venue_status ON public.walk_in_queue USING btree (venue_id, status);



CREATE INDEX idx_wallet_txn_created_by ON public.wallet_transactions USING btree (created_by);



CREATE INDEX idx_wallet_txn_created_desc ON public.wallet_transactions USING btree (created_at DESC);



CREATE INDEX idx_wallet_txn_venue ON public.wallet_transactions USING btree (venue_id);



CREATE INDEX idx_wallet_txn_wallet ON public.wallet_transactions USING btree (wallet_id);



CREATE INDEX idx_zones_venue ON public.zones USING btree (venue_id, sort_order);



CREATE INDEX partner_applications_contact_email_idx ON public.partner_applications USING btree (lower(contact_email));



CREATE INDEX partner_applications_status_created_idx ON public.partner_applications USING btree (status, created_at DESC);



CREATE UNIQUE INDEX partner_sponsor_invitations_active_sponsor_email_key ON public.partner_sponsor_invitations USING btree (sponsor_id, lower(email)) WHERE (status = 'pending'::text);



CREATE INDEX partner_sponsor_invitations_email_status_idx ON public.partner_sponsor_invitations USING btree (lower(email), status);



CREATE INDEX partner_sponsor_invitations_sponsor_status_idx ON public.partner_sponsor_invitations USING btree (sponsor_id, status);



CREATE INDEX partner_sponsor_invitations_token_hash_idx ON public.partner_sponsor_invitations USING btree (token_hash);



CREATE UNIQUE INDEX sponsor_accounts_one_active_partner_per_user_key ON public.sponsor_accounts USING btree (user_id) WHERE (status = 'active'::text);



CREATE INDEX sponsor_analytics_events_report_idx ON public.sponsor_analytics_events USING btree (sponsor_id, event_date_utc, event_type, audience_id);



CREATE INDEX sponsor_analytics_events_retention_idx ON public.sponsor_analytics_events USING btree (received_at);



CREATE INDEX sponsor_analytics_exports_lookup_idx ON public.sponsor_analytics_exports USING btree (sponsor_id, requested_by, status);



CREATE INDEX sponsor_analytics_exports_pending_idx ON public.sponsor_analytics_exports USING btree (status, requested_at) WHERE (status = ANY (ARRAY['pending'::text, 'processing'::text]));



CREATE INDEX sponsor_audience_daily_facts_report_idx ON public.sponsor_audience_daily_facts USING btree (sponsor_id, event_type, fact_date, audience_id) INCLUDE (event_count, last_event_sequence, country_code, country_provenance, age_band, age_provenance);



CREATE INDEX sponsor_audience_identities_expiry_idx ON public.sponsor_audience_identities USING btree (expires_at);



CREATE INDEX sponsor_content_daily_stats_page_idx ON public.sponsor_content_daily_stats USING btree (sponsor_id, stat_date, page_path) WHERE (page_path IS NOT NULL);



CREATE INDEX sponsor_content_daily_stats_tournament_idx ON public.sponsor_content_daily_stats USING btree (sponsor_id, stat_date, tournament_id) WHERE (tournament_id IS NOT NULL);



CREATE INDEX sponsor_device_daily_stats_report_idx ON public.sponsor_device_daily_stats USING btree (sponsor_id, stat_date) INCLUDE (device_class, impressions, clicks);



CREATE INDEX sponsor_placement_daily_stats_report_idx ON public.sponsor_placement_daily_stats USING btree (sponsor_id, stat_date) INCLUDE (placement, impressions, clicks);



CREATE INDEX sponsor_placements_global_idx ON public.sponsor_placements USING btree (placement_zone) WHERE ((tournament_id IS NULL) AND (is_active = true));



CREATE INDEX sponsor_placements_sponsor_idx ON public.sponsor_placements USING btree (sponsor_id, is_active);



CREATE INDEX sponsor_placements_tournament_active_idx ON public.sponsor_placements USING btree (tournament_id, placement_zone, is_active) WHERE (is_active = true);



CREATE UNIQUE INDEX sponsor_placements_unique_idx ON public.sponsor_placements USING btree (sponsor_id, COALESCE(tournament_id, '00000000-0000-0000-0000-000000000000'::uuid), placement_zone);



CREATE UNIQUE INDEX uidx_team_roster_members_roster_user ON public.team_roster_members USING btree (roster_id, user_id);



CREATE UNIQUE INDEX uq_br_group_teams_participant ON public.br_group_teams USING btree (group_id, participant_id) WHERE (participant_id IS NOT NULL);



CREATE UNIQUE INDEX uq_br_group_teams_team ON public.br_group_teams USING btree (group_id, team_id) WHERE (team_id IS NOT NULL);



CREATE UNIQUE INDEX uq_br_lobby_evidence_game_participant ON public.br_lobby_evidence USING btree (game_id, participant_id) WHERE (participant_id IS NOT NULL);



CREATE UNIQUE INDEX uq_br_lobby_evidence_game_team ON public.br_lobby_evidence USING btree (game_id, team_id) WHERE (team_id IS NOT NULL);



CREATE UNIQUE INDEX uq_br_lobby_readiness_lobby_participant ON public.br_lobby_readiness USING btree (lobby_id, participant_id) WHERE ((participant_id IS NOT NULL) AND (game_id IS NULL));



CREATE UNIQUE INDEX uq_br_lobby_readiness_lobby_team ON public.br_lobby_readiness USING btree (lobby_id, team_id) WHERE ((team_id IS NOT NULL) AND (game_id IS NULL));



CREATE UNIQUE INDEX uq_br_lobby_results_game_participant ON public.br_lobby_results USING btree (game_id, participant_id) WHERE (participant_id IS NOT NULL);



CREATE UNIQUE INDEX uq_br_lobby_results_game_placement ON public.br_lobby_results USING btree (game_id, placement);



CREATE UNIQUE INDEX uq_br_lobby_results_game_team ON public.br_lobby_results USING btree (game_id, team_id) WHERE (team_id IS NOT NULL);



CREATE UNIQUE INDEX uq_gdpr_active_request ON public.gdpr_requests USING btree (user_id, request_type) WHERE (status = ANY (ARRAY['pending'::text, 'processing'::text]));



CREATE UNIQUE INDEX uq_user_roles_user_role ON public.user_roles USING btree (user_id, role);



CREATE UNIQUE INDEX uq_verified_roles_user_role ON public.verified_roles USING btree (user_id, role);



CREATE UNIQUE INDEX ux_game_catalog_aliases_version_alias ON public.game_catalog_game_aliases USING btree (version_id, lower(alias));



CREATE UNIQUE INDEX ux_game_catalog_versions_active ON public.game_catalog_versions USING btree (is_active) WHERE (is_active = true);



CREATE UNIQUE INDEX ux_game_catalog_versions_content_hash ON public.game_catalog_versions USING btree (content_hash);



CREATE UNIQUE INDEX ux_game_catalog_versions_single_draft ON public.game_catalog_versions USING btree (status) WHERE (status = 'draft'::text);



CREATE UNIQUE INDEX ux_tournament_invitation_active_email ON public.tournament_invitations USING btree (tournament_id, lower(email)) WHERE (status <> 'revoked'::text);



CREATE UNIQUE INDEX ux_tournament_invitations_code ON public.tournament_invitations USING btree (code);



CREATE TRIGGER on_business_role_approved AFTER INSERT OR UPDATE ON public.verified_roles FOR EACH ROW EXECUTE FUNCTION public.generate_profile_license_id();



CREATE TRIGGER on_sponsor_interaction AFTER INSERT ON public.sponsor_impressions FOR EACH ROW EXECUTE FUNCTION public.handle_new_sponsor_interaction();



CREATE TRIGGER organizations_updated_at BEFORE UPDATE ON public.organizations FOR EACH ROW EXECUTE FUNCTION public.update_organizations_updated_at();



CREATE TRIGGER set_license_id BEFORE INSERT ON public.licenses FOR EACH ROW EXECUTE FUNCTION public.generate_license_id();



CREATE TRIGGER set_pairing_token BEFORE INSERT ON public.venues FOR EACH ROW EXECUTE FUNCTION public.generate_pairing_token();



CREATE TRIGGER set_tournament_stages_updated_at BEFORE UPDATE ON public.tournament_stages FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();



CREATE TRIGGER set_venue_id BEFORE INSERT ON public.venues FOR EACH ROW EXECUTE FUNCTION public.generate_venue_id();



CREATE TRIGGER sponsor_analytics_events_no_update BEFORE UPDATE ON public.sponsor_analytics_events FOR EACH ROW EXECUTE FUNCTION public.reject_sponsor_analytics_event_update();



CREATE TRIGGER tr_sync_tournament_organizer BEFORE INSERT OR UPDATE OF organization_id ON public.tournaments FOR EACH ROW EXECUTE FUNCTION public.fn_sync_tournament_organizer();



CREATE TRIGGER trg_aggregate_sponsor_impression AFTER INSERT ON public.sponsor_impressions FOR EACH ROW EXECUTE FUNCTION public.fn_aggregate_sponsor_impression();



CREATE TRIGGER trg_auto_earn_loyalty AFTER UPDATE OF ended_at ON public.venue_sessions FOR EACH ROW WHEN (((old.ended_at IS NULL) AND (new.ended_at IS NOT NULL))) EXECUTE FUNCTION public.auto_earn_loyalty_on_session_end();



CREATE TRIGGER trg_block_organizer_tournament_updates BEFORE UPDATE ON public.tournaments FOR EACH ROW EXECUTE FUNCTION public.block_organizer_sensitive_tournament_updates();



CREATE TRIGGER trg_block_team_stats_update BEFORE UPDATE ON public.teams FOR EACH ROW EXECUTE FUNCTION public.block_team_stats_update();



CREATE TRIGGER trg_block_venue_booking_payment_spoofing BEFORE UPDATE ON public.venue_bookings FOR EACH ROW EXECUTE FUNCTION public.block_venue_booking_payment_spoofing();



CREATE TRIGGER trg_br_lobby_evidence_updated_at BEFORE UPDATE ON public.br_lobby_evidence FOR EACH ROW EXECUTE FUNCTION public.update_br_lobby_evidence_updated_at();



CREATE TRIGGER trg_cascade_stage_bestof AFTER UPDATE ON public.tournament_stages FOR EACH ROW EXECUTE FUNCTION public.cascade_stage_bestof_to_vetos();



CREATE TRIGGER trg_customer_wallets_updated_at BEFORE UPDATE ON public.customer_wallets FOR EACH ROW EXECUTE FUNCTION public.update_customer_wallets_updated_at();



CREATE TRIGGER trg_daily_stats_updated_at BEFORE UPDATE ON public.daily_stats FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();



CREATE TRIGGER trg_enforce_roster_member_limits BEFORE INSERT ON public.team_roster_members FOR EACH ROW EXECUTE FUNCTION public.enforce_roster_member_limits();



CREATE TRIGGER trg_enforce_roster_member_limits_update BEFORE UPDATE OF roster_role, is_starter ON public.team_roster_members FOR EACH ROW EXECUTE FUNCTION public.enforce_roster_member_limits();



CREATE TRIGGER trg_game_servers_updated_at BEFORE UPDATE ON public.game_servers FOR EACH ROW EXECUTE FUNCTION public.update_game_servers_updated_at();



CREATE TRIGGER trg_loyalty_accounts_updated_at BEFORE UPDATE ON public.loyalty_accounts FOR EACH ROW EXECUTE FUNCTION public.update_loyalty_accounts_updated_at();



CREATE TRIGGER trg_normalize_booking_code BEFORE INSERT OR UPDATE ON public.venue_bookings FOR EACH ROW EXECUTE FUNCTION public.normalize_booking_code();



CREATE TRIGGER trg_pos_orders_updated_at BEFORE UPDATE ON public.pos_orders FOR EACH ROW EXECUTE FUNCTION public.update_pos_orders_updated_at();



CREATE TRIGGER trg_report_schedules_updated_at BEFORE UPDATE ON public.report_schedules FOR EACH ROW EXECUTE FUNCTION public.update_report_schedules_updated_at();



CREATE TRIGGER trg_sync_admin_profile AFTER INSERT OR DELETE OR UPDATE ON public.admin_user_roles FOR EACH ROW EXECUTE FUNCTION public.fn_sync_admin_profile();



CREATE TRIGGER trg_sync_veto_bestof BEFORE INSERT OR UPDATE OF stage_id ON public.match_map_vetos FOR EACH ROW EXECUTE FUNCTION public.sync_veto_bestof_from_stage();



CREATE TRIGGER trg_system_config_updated_at BEFORE UPDATE ON public.system_config FOR EACH ROW EXECUTE FUNCTION public.touch_system_config_updated_at();



CREATE TRIGGER trg_system_settings_updated_at BEFORE UPDATE ON public.system_settings FOR EACH ROW EXECUTE FUNCTION public.update_system_settings_timestamp();



CREATE TRIGGER trg_venue_announcements_updated_at BEFORE UPDATE ON public.venue_announcements FOR EACH ROW EXECUTE FUNCTION public.update_venue_announcements_updated_at();



CREATE TRIGGER trg_venue_combos_updated_at BEFORE UPDATE ON public.venue_combos FOR EACH ROW EXECUTE FUNCTION public.update_venue_combos_updated_at();



CREATE TRIGGER trg_venue_loyalty_config_updated_at BEFORE UPDATE ON public.venue_loyalty_config FOR EACH ROW EXECUTE FUNCTION public.update_venue_loyalty_config_updated_at();



CREATE TRIGGER trg_venue_menu_items_updated_at BEFORE UPDATE ON public.venue_menu_items FOR EACH ROW EXECUTE FUNCTION public.update_venue_menu_items_updated_at();



CREATE TRIGGER trg_venue_packages_updated_at BEFORE UPDATE ON public.venue_packages FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();



CREATE TRIGGER trigger_advance_bracket_db BEFORE INSERT ON public.match_completed_events FOR EACH ROW EXECUTE FUNCTION public.proc_advance_bracket_match();



CREATE TRIGGER trigger_match_completed_events AFTER INSERT ON public.match_completed_events FOR EACH ROW EXECUTE FUNCTION public.trigger_match_completed_webhook();



CREATE TRIGGER trigger_set_stage1_capacity BEFORE INSERT ON public.tournament_stages FOR EACH ROW EXECUTE FUNCTION public.set_stage1_initial_capacity();



CREATE TRIGGER trigger_sync_stage1_capacity AFTER UPDATE ON public.tournaments FOR EACH ROW EXECUTE FUNCTION public.sync_stage1_capacity();



CREATE TRIGGER trigger_validate_roster_name BEFORE INSERT OR UPDATE ON public.tournament_participants FOR EACH ROW EXECUTE FUNCTION public.validate_roster_name_match();



CREATE TRIGGER update_profiles_updated_at BEFORE UPDATE ON public.profiles FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();



CREATE TRIGGER update_teams_updated_at BEFORE UPDATE ON public.teams FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();



CREATE TRIGGER update_tournament_bans_updated_at BEFORE UPDATE ON public.tournament_bans FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();



CREATE TRIGGER update_tournament_participants_updated_at BEFORE UPDATE ON public.tournament_participants FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();



CREATE TRIGGER update_tournaments_updated_at BEFORE UPDATE ON public.tournaments FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();



CREATE TRIGGER update_venue_bookings_updated_at BEFORE UPDATE ON public.venue_bookings FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();



CREATE TRIGGER update_venues_updated_at BEFORE UPDATE ON public.venues FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();



CREATE TRIGGER update_verification_requests_updated_at BEFORE UPDATE ON public.verification_requests FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();



ALTER TABLE ONLY public.account_security_state
    ADD CONSTRAINT account_security_state_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.activity_log
    ADD CONSTRAINT activity_log_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.admin_alerts
    ADD CONSTRAINT admin_alerts_acknowledged_by_fkey FOREIGN KEY (acknowledged_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.admin_alerts
    ADD CONSTRAINT admin_alerts_resolved_by_fkey FOREIGN KEY (resolved_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.admin_dashboard_preferences
    ADD CONSTRAINT admin_dashboard_preferences_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.admin_game_assignments
    ADD CONSTRAINT admin_game_assignments_admin_id_fkey FOREIGN KEY (admin_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.admin_game_assignments
    ADD CONSTRAINT admin_game_assignments_assigned_by_fkey FOREIGN KEY (assigned_by) REFERENCES public.profiles(id);



ALTER TABLE ONLY public.admin_impersonation_sessions
    ADD CONSTRAINT admin_impersonation_sessions_admin_id_fkey FOREIGN KEY (admin_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.admin_impersonation_sessions
    ADD CONSTRAINT admin_impersonation_sessions_target_user_id_fkey FOREIGN KEY (target_user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.admin_ip_allowlist
    ADD CONSTRAINT admin_ip_allowlist_created_by_fkey FOREIGN KEY (created_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.admin_role_permissions
    ADD CONSTRAINT admin_role_permissions_permission_id_fkey FOREIGN KEY (permission_id) REFERENCES public.admin_permissions(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.admin_role_permissions
    ADD CONSTRAINT admin_role_permissions_role_id_fkey FOREIGN KEY (role_id) REFERENCES public.admin_roles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.admin_session_audit
    ADD CONSTRAINT admin_session_audit_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.admin_user_roles
    ADD CONSTRAINT admin_user_roles_assigned_by_fkey FOREIGN KEY (assigned_by) REFERENCES public.profiles(id);



ALTER TABLE ONLY public.admin_user_roles
    ADD CONSTRAINT admin_user_roles_role_id_fkey FOREIGN KEY (role_id) REFERENCES public.admin_roles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.admin_user_roles
    ADD CONSTRAINT admin_user_roles_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.anomaly_events
    ADD CONSTRAINT anomaly_events_resolved_by_fkey FOREIGN KEY (resolved_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.anomaly_events
    ADD CONSTRAINT anomaly_events_rule_id_fkey FOREIGN KEY (rule_id) REFERENCES public.anomaly_rules(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.balance_transactions
    ADD CONSTRAINT balance_transactions_session_id_fkey FOREIGN KEY (session_id) REFERENCES public.venue_sessions(id);



ALTER TABLE ONLY public.balance_transactions
    ADD CONSTRAINT balance_transactions_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.balance_transactions
    ADD CONSTRAINT balance_transactions_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id);



ALTER TABLE ONLY public.booking_rules
    ADD CONSTRAINT booking_rules_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_game_data
    ADD CONSTRAINT br_game_data_updated_by_fkey FOREIGN KEY (updated_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.br_games
    ADD CONSTRAINT br_games_lobby_id_fkey FOREIGN KEY (lobby_id) REFERENCES public.br_lobbies(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_group_teams
    ADD CONSTRAINT br_group_teams_group_id_fkey FOREIGN KEY (group_id) REFERENCES public.br_groups(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_group_teams
    ADD CONSTRAINT br_group_teams_participant_id_fkey FOREIGN KEY (participant_id) REFERENCES public.tournament_participants(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_group_teams
    ADD CONSTRAINT br_group_teams_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_groups
    ADD CONSTRAINT br_groups_stage_id_fkey FOREIGN KEY (stage_id) REFERENCES public.tournament_stages(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_lobbies
    ADD CONSTRAINT br_lobbies_stage_id_fkey FOREIGN KEY (stage_id) REFERENCES public.tournament_stages(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_lobby_evidence
    ADD CONSTRAINT br_lobby_evidence_game_id_fkey FOREIGN KEY (game_id) REFERENCES public.br_games(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_lobby_groups
    ADD CONSTRAINT br_lobby_groups_group_id_fkey FOREIGN KEY (group_id) REFERENCES public.br_groups(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_lobby_groups
    ADD CONSTRAINT br_lobby_groups_lobby_id_fkey FOREIGN KEY (lobby_id) REFERENCES public.br_lobbies(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_lobby_readiness
    ADD CONSTRAINT br_lobby_readiness_game_id_fkey FOREIGN KEY (game_id) REFERENCES public.br_games(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_lobby_readiness
    ADD CONSTRAINT br_lobby_readiness_lobby_id_fkey FOREIGN KEY (lobby_id) REFERENCES public.br_lobbies(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_lobby_readiness
    ADD CONSTRAINT br_lobby_readiness_participant_id_fkey FOREIGN KEY (participant_id) REFERENCES public.tournament_participants(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_lobby_readiness
    ADD CONSTRAINT br_lobby_readiness_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_lobby_readiness
    ADD CONSTRAINT br_lobby_readiness_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_lobby_results
    ADD CONSTRAINT br_lobby_results_game_id_fkey FOREIGN KEY (game_id) REFERENCES public.br_games(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_lobby_evidence
    ADD CONSTRAINT br_round_evidence_participant_id_fkey FOREIGN KEY (participant_id) REFERENCES public.tournament_participants(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_lobby_evidence
    ADD CONSTRAINT br_round_evidence_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_lobby_results
    ADD CONSTRAINT br_round_results_participant_id_fkey FOREIGN KEY (participant_id) REFERENCES public.tournament_participants(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.br_lobby_results
    ADD CONSTRAINT br_round_results_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.brkt_advancements
    ADD CONSTRAINT brkt_advancements_source_match_id_fkey FOREIGN KEY (source_match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.brkt_advancements
    ADD CONSTRAINT brkt_advancements_target_match_id_fkey FOREIGN KEY (target_match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.brkt_advancements
    ADD CONSTRAINT brkt_advancements_version_id_fkey FOREIGN KEY (version_id) REFERENCES public.brkt_versions(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.brkt_layout
    ADD CONSTRAINT brkt_layout_match_id_fkey FOREIGN KEY (match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.brkt_layout
    ADD CONSTRAINT brkt_layout_version_id_fkey FOREIGN KEY (version_id) REFERENCES public.brkt_versions(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.brkt_match_events
    ADD CONSTRAINT brkt_match_events_created_by_fkey FOREIGN KEY (created_by) REFERENCES public.profiles(id);



ALTER TABLE ONLY public.brkt_match_events
    ADD CONSTRAINT brkt_match_events_match_id_fkey FOREIGN KEY (match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.brkt_match_games
    ADD CONSTRAINT brkt_match_games_loser_id_fkey FOREIGN KEY (loser_id) REFERENCES public.teams(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.brkt_match_games
    ADD CONSTRAINT brkt_match_games_map_id_fkey FOREIGN KEY (map_id) REFERENCES public.game_maps(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.brkt_match_games
    ADD CONSTRAINT brkt_match_games_match_id_fkey FOREIGN KEY (match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.brkt_match_games
    ADD CONSTRAINT brkt_match_games_mvp_id_fkey FOREIGN KEY (mvp_id) REFERENCES public.profiles(id);



ALTER TABLE ONLY public.brkt_match_games
    ADD CONSTRAINT brkt_match_games_reported_by_team_id_fkey FOREIGN KEY (reported_by_team_id) REFERENCES public.teams(id);



ALTER TABLE ONLY public.brkt_match_games
    ADD CONSTRAINT brkt_match_games_winner_id_fkey FOREIGN KEY (winner_id) REFERENCES public.teams(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.brkt_matches
    ADD CONSTRAINT brkt_matches_version_id_fkey FOREIGN KEY (version_id) REFERENCES public.brkt_versions(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.brkt_versions
    ADD CONSTRAINT brkt_versions_tournament_id_fkey FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.broadcast_deliveries
    ADD CONSTRAINT broadcast_deliveries_broadcast_id_fkey FOREIGN KEY (broadcast_id) REFERENCES public.broadcasts(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.broadcast_deliveries
    ADD CONSTRAINT broadcast_deliveries_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.broadcast_licenses
    ADD CONSTRAINT broadcast_licenses_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.broadcast_overlay_layouts
    ADD CONSTRAINT broadcast_overlay_layouts_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.broadcast_templates
    ADD CONSTRAINT broadcast_templates_created_by_fkey FOREIGN KEY (created_by) REFERENCES public.profiles(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.broadcast_themes
    ADD CONSTRAINT broadcast_themes_owner_id_fkey FOREIGN KEY (owner_id) REFERENCES auth.users(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.broadcasts
    ADD CONSTRAINT broadcasts_created_by_fkey FOREIGN KEY (created_by) REFERENCES public.profiles(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.consent_records
    ADD CONSTRAINT consent_records_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users(id);



ALTER TABLE ONLY public.customer_wallets
    ADD CONSTRAINT customer_wallets_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.customer_wallets
    ADD CONSTRAINT customer_wallets_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.daily_sponsor_stats
    ADD CONSTRAINT daily_sponsor_stats_sponsor_id_fkey FOREIGN KEY (sponsor_id) REFERENCES public.sponsors(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.daily_stats
    ADD CONSTRAINT daily_stats_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.dispute_comments
    ADD CONSTRAINT dispute_comments_dispute_id_fkey FOREIGN KEY (dispute_id) REFERENCES public.tournament_disputes(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.dispute_read_receipts
    ADD CONSTRAINT dispute_read_receipts_dispute_id_fkey FOREIGN KEY (dispute_id) REFERENCES public.tournament_disputes(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.dispute_read_receipts
    ADD CONSTRAINT dispute_read_receipts_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.disputes
    ADD CONSTRAINT disputes_match_id_fkey FOREIGN KEY (match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.disputes
    ADD CONSTRAINT disputes_reporter_id_fkey FOREIGN KEY (reporter_id) REFERENCES public.profiles(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.disputes
    ADD CONSTRAINT disputes_resolved_by_fkey FOREIGN KEY (resolved_by) REFERENCES public.profiles(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.disputes
    ADD CONSTRAINT disputes_tournament_id_fkey FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.feature_flag_overrides
    ADD CONSTRAINT feature_flag_overrides_created_by_fkey FOREIGN KEY (created_by) REFERENCES public.profiles(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.feature_flag_overrides
    ADD CONSTRAINT feature_flag_overrides_flag_id_fkey FOREIGN KEY (flag_id) REFERENCES public.feature_flags(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.feature_flag_overrides
    ADD CONSTRAINT feature_flag_overrides_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.feature_flag_rules
    ADD CONSTRAINT feature_flag_rules_flag_id_fkey FOREIGN KEY (flag_id) REFERENCES public.feature_flags(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.feature_flags
    ADD CONSTRAINT feature_flags_created_by_fkey FOREIGN KEY (created_by) REFERENCES public.profiles(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.game_servers
    ADD CONSTRAINT fk_game_servers_match FOREIGN KEY (match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.game_servers
    ADD CONSTRAINT fk_game_servers_tournament FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.match_player_stats
    ADD CONSTRAINT fk_match_player_stats_match FOREIGN KEY (match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.match_player_stats
    ADD CONSTRAINT fk_match_player_stats_user FOREIGN KEY (user_id) REFERENCES auth.users(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.session_invoices
    ADD CONSTRAINT fk_session_invoices_member FOREIGN KEY (member_id) REFERENCES public.members(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.venue_sessions
    ADD CONSTRAINT fk_venue_sessions_member FOREIGN KEY (member_id) REFERENCES public.members(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.game_catalog_game_aliases
    ADD CONSTRAINT game_catalog_game_aliases_version_id_fkey FOREIGN KEY (version_id) REFERENCES public.game_catalog_versions(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.game_catalog_game_modes
    ADD CONSTRAINT game_catalog_game_modes_version_id_fkey FOREIGN KEY (version_id) REFERENCES public.game_catalog_versions(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.game_catalog_games
    ADD CONSTRAINT game_catalog_games_version_id_fkey FOREIGN KEY (version_id) REFERENCES public.game_catalog_versions(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.game_catalog_tournament_structures
    ADD CONSTRAINT game_catalog_tournament_structures_version_id_fkey FOREIGN KEY (version_id) REFERENCES public.game_catalog_versions(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.gdpr_requests
    ADD CONSTRAINT gdpr_requests_processed_by_fkey FOREIGN KEY (processed_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.gdpr_requests
    ADD CONSTRAINT gdpr_requests_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users(id);



ALTER TABLE ONLY public.ghost_approvals
    ADD CONSTRAINT ghost_approvals_approved_by_fkey FOREIGN KEY (approved_by) REFERENCES public.profiles(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.ghost_approvals
    ADD CONSTRAINT ghost_approvals_requester_id_fkey FOREIGN KEY (requester_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.ghost_approvals
    ADD CONSTRAINT ghost_approvals_target_user_id_fkey FOREIGN KEY (target_user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.ghost_data_access_log
    ADD CONSTRAINT ghost_data_access_log_session_id_fkey FOREIGN KEY (session_id) REFERENCES public.ghost_sessions(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.ghost_sessions
    ADD CONSTRAINT ghost_sessions_admin_id_fkey FOREIGN KEY (admin_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.ghost_sessions
    ADD CONSTRAINT ghost_sessions_approval_id_fkey FOREIGN KEY (approval_id) REFERENCES public.ghost_approvals(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.ghost_sessions
    ADD CONSTRAINT ghost_sessions_target_user_id_fkey FOREIGN KEY (target_user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.leaderboard
    ADD CONSTRAINT leaderboard_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.licenses
    ADD CONSTRAINT licenses_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.loyalty_accounts
    ADD CONSTRAINT loyalty_accounts_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.loyalty_transactions
    ADD CONSTRAINT loyalty_transactions_account_id_fkey FOREIGN KEY (account_id) REFERENCES public.loyalty_accounts(id) ON DELETE RESTRICT;



ALTER TABLE ONLY public.loyalty_transactions
    ADD CONSTRAINT loyalty_transactions_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id);



ALTER TABLE ONLY public.match_checkins
    ADD CONSTRAINT match_checkins_match_id_fkey FOREIGN KEY (match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.match_checkins
    ADD CONSTRAINT match_checkins_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.match_checkins
    ADD CONSTRAINT match_checkins_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users(id);



ALTER TABLE ONLY public.match_completed_events
    ADD CONSTRAINT match_completed_events_loser_id_fkey FOREIGN KEY (loser_id) REFERENCES public.teams(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.match_completed_events
    ADD CONSTRAINT match_completed_events_match_id_fkey FOREIGN KEY (match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.match_completed_events
    ADD CONSTRAINT match_completed_events_winner_id_fkey FOREIGN KEY (winner_id) REFERENCES public.teams(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.match_disputes
    ADD CONSTRAINT match_disputes_disputed_by_team_id_fkey FOREIGN KEY (disputed_by_team_id) REFERENCES public.teams(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.match_disputes
    ADD CONSTRAINT match_disputes_disputed_by_user_id_fkey FOREIGN KEY (disputed_by_user_id) REFERENCES public.profiles(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.match_disputes
    ADD CONSTRAINT match_disputes_resolved_by_fkey FOREIGN KEY (resolved_by) REFERENCES public.profiles(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.match_map_veto_actions
    ADD CONSTRAINT match_map_veto_actions_map_id_fkey1 FOREIGN KEY (map_id) REFERENCES public.game_maps(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.match_map_veto_actions
    ADD CONSTRAINT match_map_veto_actions_match_id_fkey1 FOREIGN KEY (match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.match_map_veto_actions
    ADD CONSTRAINT match_map_veto_actions_team_id_fkey1 FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.match_map_veto_actions
    ADD CONSTRAINT match_map_veto_actions_veto_id_fkey FOREIGN KEY (veto_id) REFERENCES public.match_map_vetos(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.match_map_vetos
    ADD CONSTRAINT match_map_vetos_current_team_id_fkey1 FOREIGN KEY (current_team_id) REFERENCES public.teams(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.match_map_vetos
    ADD CONSTRAINT match_map_vetos_match_id_fkey FOREIGN KEY (match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.match_map_vetos
    ADD CONSTRAINT match_map_vetos_match_id_fkey_new FOREIGN KEY (match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.match_map_vetos
    ADD CONSTRAINT match_map_vetos_selected_map_id_fkey1 FOREIGN KEY (selected_map_id) REFERENCES public.game_maps(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.match_map_vetos
    ADD CONSTRAINT match_map_vetos_stage_id_fkey FOREIGN KEY (stage_id) REFERENCES public.tournament_stages(id);



ALTER TABLE ONLY public.match_map_vetos
    ADD CONSTRAINT match_map_vetos_team1_id_fkey1 FOREIGN KEY (team1_id) REFERENCES public.teams(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.match_map_vetos
    ADD CONSTRAINT match_map_vetos_team2_id_fkey1 FOREIGN KEY (team2_id) REFERENCES public.teams(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.match_map_vetos
    ADD CONSTRAINT match_map_vetos_tournament_id_fkey1 FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.match_messages
    ADD CONSTRAINT match_messages_match_id_fkey FOREIGN KEY (match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.match_messages
    ADD CONSTRAINT match_messages_sender_id_fkey FOREIGN KEY (sender_id) REFERENCES auth.users(id);



ALTER TABLE ONLY public.match_result_reports
    ADD CONSTRAINT match_result_reports_map_id_fkey FOREIGN KEY (map_id) REFERENCES public.game_maps(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.match_result_reports
    ADD CONSTRAINT match_result_reports_match_id_fkey FOREIGN KEY (match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.match_result_reports
    ADD CONSTRAINT match_result_reports_reported_by_fkey FOREIGN KEY (reported_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.match_result_reports
    ADD CONSTRAINT match_result_reports_responded_by_fkey FOREIGN KEY (responded_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.match_time_proposals
    ADD CONSTRAINT match_time_proposals_match_id_fkey FOREIGN KEY (match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.member_packages
    ADD CONSTRAINT member_packages_member_id_fkey FOREIGN KEY (member_id) REFERENCES public.members(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.member_packages
    ADD CONSTRAINT member_packages_package_id_fkey FOREIGN KEY (package_id) REFERENCES public.venue_packages(id);



ALTER TABLE ONLY public.member_packages
    ADD CONSTRAINT member_packages_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.members
    ADD CONSTRAINT members_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users(id);



ALTER TABLE ONLY public.members
    ADD CONSTRAINT members_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.moderation_queue
    ADD CONSTRAINT moderation_queue_reported_by_fkey FOREIGN KEY (reported_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.moderation_queue
    ADD CONSTRAINT moderation_queue_reviewed_by_fkey FOREIGN KEY (reviewed_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.notification_preferences
    ADD CONSTRAINT notification_preferences_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.notifications
    ADD CONSTRAINT notifications_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.organization_albums
    ADD CONSTRAINT organization_albums_organization_id_fkey FOREIGN KEY (organization_id) REFERENCES public.organizations(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.organization_media
    ADD CONSTRAINT organization_media_album_id_fkey FOREIGN KEY (album_id) REFERENCES public.organization_albums(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.organization_media
    ADD CONSTRAINT organization_media_organization_id_fkey FOREIGN KEY (organization_id) REFERENCES public.organizations(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.organization_staff
    ADD CONSTRAINT organization_staff_assigned_by_fkey FOREIGN KEY (assigned_by) REFERENCES public.profiles(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.organization_staff
    ADD CONSTRAINT organization_staff_organization_id_fkey FOREIGN KEY (organization_id) REFERENCES public.organizations(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.organization_staff
    ADD CONSTRAINT organization_staff_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.organizations
    ADD CONSTRAINT organizations_owner_id_fkey FOREIGN KEY (owner_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.partner_applications
    ADD CONSTRAINT partner_applications_approved_by_fkey FOREIGN KEY (approved_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.partner_applications
    ADD CONSTRAINT partner_applications_approved_sponsor_id_fkey FOREIGN KEY (approved_sponsor_id) REFERENCES public.sponsors(id);



ALTER TABLE ONLY public.partner_applications
    ADD CONSTRAINT partner_applications_rejected_by_fkey FOREIGN KEY (rejected_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.partner_applications
    ADD CONSTRAINT partner_applications_reviewed_by_fkey FOREIGN KEY (reviewed_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.partner_sponsor_invitations
    ADD CONSTRAINT partner_sponsor_invitations_accepted_by_user_id_fkey FOREIGN KEY (accepted_by_user_id) REFERENCES auth.users(id);



ALTER TABLE ONLY public.partner_sponsor_invitations
    ADD CONSTRAINT partner_sponsor_invitations_invited_by_fkey FOREIGN KEY (invited_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.partner_sponsor_invitations
    ADD CONSTRAINT partner_sponsor_invitations_sponsor_id_fkey FOREIGN KEY (sponsor_id) REFERENCES public.sponsors(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.player_balance
    ADD CONSTRAINT player_balance_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.player_steam_accounts
    ADD CONSTRAINT player_steam_accounts_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.pos_orders
    ADD CONSTRAINT pos_orders_member_id_fkey FOREIGN KEY (member_id) REFERENCES public.members(id);



ALTER TABLE ONLY public.pos_orders
    ADD CONSTRAINT pos_orders_session_id_fkey FOREIGN KEY (session_id) REFERENCES public.venue_sessions(id);



ALTER TABLE ONLY public.pos_orders
    ADD CONSTRAINT pos_orders_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.public_tool_bracket_versions
    ADD CONSTRAINT public_tool_bracket_versions_bracket_id_fkey FOREIGN KEY (bracket_id) REFERENCES public.public_tool_brackets(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.public_tool_brackets
    ADD CONSTRAINT public_tool_brackets_owner_user_id_fkey FOREIGN KEY (owner_user_id) REFERENCES auth.users(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.public_veto_actions
    ADD CONSTRAINT public_veto_actions_session_id_fkey FOREIGN KEY (session_id) REFERENCES public.public_veto_sessions(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.report_run_log
    ADD CONSTRAINT report_run_log_schedule_id_fkey FOREIGN KEY (schedule_id) REFERENCES public.report_schedules(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.report_schedules
    ADD CONSTRAINT report_schedules_created_by_fkey FOREIGN KEY (created_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.reviews
    ADD CONSTRAINT reviews_reviewee_id_fkey FOREIGN KEY (reviewee_id) REFERENCES auth.users(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.reviews
    ADD CONSTRAINT reviews_reviewer_id_fkey FOREIGN KEY (reviewer_id) REFERENCES auth.users(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.reviews
    ADD CONSTRAINT reviews_tournament_id_fkey FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.reviews
    ADD CONSTRAINT reviews_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.revoked_sessions
    ADD CONSTRAINT revoked_sessions_revoked_by_fkey FOREIGN KEY (revoked_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.revoked_sessions
    ADD CONSTRAINT revoked_sessions_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.riot_accounts
    ADD CONSTRAINT riot_accounts_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.session_invoices
    ADD CONSTRAINT session_invoices_session_id_fkey FOREIGN KEY (session_id) REFERENCES public.venue_sessions(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.session_invoices
    ADD CONSTRAINT session_invoices_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.session_refunds
    ADD CONSTRAINT session_refunds_refunded_by_fkey FOREIGN KEY (refunded_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.session_refunds
    ADD CONSTRAINT session_refunds_session_id_fkey FOREIGN KEY (session_id) REFERENCES public.venue_sessions(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.session_refunds
    ADD CONSTRAINT session_refunds_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.session_refunds
    ADD CONSTRAINT session_refunds_wallet_txn_id_fkey FOREIGN KEY (wallet_txn_id) REFERENCES public.wallet_transactions(id);



ALTER TABLE ONLY public.sponsor_accounts
    ADD CONSTRAINT sponsor_accounts_invitation_id_fkey FOREIGN KEY (invitation_id) REFERENCES public.partner_sponsor_invitations(id);



ALTER TABLE ONLY public.sponsor_accounts
    ADD CONSTRAINT sponsor_accounts_sponsor_id_fkey FOREIGN KEY (sponsor_id) REFERENCES public.sponsors(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.sponsor_analytics_events
    ADD CONSTRAINT sponsor_analytics_events_sponsor_id_fkey FOREIGN KEY (sponsor_id) REFERENCES public.sponsors(id) ON DELETE RESTRICT;



ALTER TABLE ONLY public.sponsor_analytics_events
    ADD CONSTRAINT sponsor_analytics_events_tournament_id_fkey FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.sponsor_analytics_exports
    ADD CONSTRAINT sponsor_analytics_exports_sponsor_id_fkey FOREIGN KEY (sponsor_id) REFERENCES public.sponsors(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.sponsor_audience_daily_facts
    ADD CONSTRAINT sponsor_audience_daily_facts_sponsor_id_fkey FOREIGN KEY (sponsor_id) REFERENCES public.sponsors(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.sponsor_audience_identities
    ADD CONSTRAINT sponsor_audience_identities_sponsor_id_fkey FOREIGN KEY (sponsor_id) REFERENCES public.sponsors(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.sponsor_content_daily_stats
    ADD CONSTRAINT sponsor_content_daily_stats_sponsor_id_fkey FOREIGN KEY (sponsor_id) REFERENCES public.sponsors(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.sponsor_content_daily_stats
    ADD CONSTRAINT sponsor_content_daily_stats_tournament_id_fkey FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.sponsor_daily_totals
    ADD CONSTRAINT sponsor_daily_totals_sponsor_id_fkey FOREIGN KEY (sponsor_id) REFERENCES public.sponsors(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.sponsor_device_daily_stats
    ADD CONSTRAINT sponsor_device_daily_stats_sponsor_id_fkey FOREIGN KEY (sponsor_id) REFERENCES public.sponsors(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.sponsor_impressions
    ADD CONSTRAINT sponsor_impressions_sponsor_id_fkey FOREIGN KEY (sponsor_id) REFERENCES public.sponsors(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.sponsor_impressions
    ADD CONSTRAINT sponsor_impressions_tournament_id_fkey FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.sponsor_placement_daily_stats
    ADD CONSTRAINT sponsor_placement_daily_stats_sponsor_id_fkey FOREIGN KEY (sponsor_id) REFERENCES public.sponsors(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.sponsor_placements
    ADD CONSTRAINT sponsor_placements_assigned_by_fkey FOREIGN KEY (assigned_by) REFERENCES public.profiles(id);



ALTER TABLE ONLY public.sponsor_placements
    ADD CONSTRAINT sponsor_placements_sponsor_id_fkey FOREIGN KEY (sponsor_id) REFERENCES public.sponsors(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.sponsor_placements
    ADD CONSTRAINT sponsor_placements_tournament_id_fkey FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.staff_audit_log
    ADD CONSTRAINT staff_audit_log_actor_id_fkey FOREIGN KEY (actor_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.staff_audit_log
    ADD CONSTRAINT staff_audit_log_organization_id_fkey FOREIGN KEY (organization_id) REFERENCES public.organizations(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.staff_permissions
    ADD CONSTRAINT staff_permissions_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.staff_shifts
    ADD CONSTRAINT staff_shifts_staff_id_fkey FOREIGN KEY (staff_id) REFERENCES public.venue_staff(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.staff_shifts
    ADD CONSTRAINT staff_shifts_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.staff_tournament_assignments
    ADD CONSTRAINT staff_tournament_assignments_assigned_by_fkey FOREIGN KEY (assigned_by) REFERENCES public.profiles(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.staff_tournament_assignments
    ADD CONSTRAINT staff_tournament_assignments_organization_staff_id_fkey FOREIGN KEY (organization_staff_id) REFERENCES public.organization_staff(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.staff_tournament_assignments
    ADD CONSTRAINT staff_tournament_assignments_tournament_id_fkey FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.stage_participants
    ADD CONSTRAINT stage_participants_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.station_health_snapshots
    ADD CONSTRAINT station_health_snapshots_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.system_config
    ADD CONSTRAINT system_config_updated_by_fkey FOREIGN KEY (updated_by) REFERENCES public.profiles(id);



ALTER TABLE ONLY public.system_settings
    ADD CONSTRAINT system_settings_updated_by_fkey FOREIGN KEY (updated_by) REFERENCES public.profiles(id);



ALTER TABLE ONLY public.team_invitations
    ADD CONSTRAINT team_invitations_invited_by_fkey FOREIGN KEY (invited_by) REFERENCES public.profiles(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.team_invitations
    ADD CONSTRAINT team_invitations_invited_by_user_id_fkey FOREIGN KEY (invited_by_user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.team_invitations
    ADD CONSTRAINT team_invitations_invited_user_id_fkey FOREIGN KEY (invited_user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.team_invitations
    ADD CONSTRAINT team_invitations_roster_id_fkey FOREIGN KEY (roster_id) REFERENCES public.team_rosters(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.team_invitations
    ADD CONSTRAINT team_invitations_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.team_members
    ADD CONSTRAINT team_members_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.team_members
    ADD CONSTRAINT team_members_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.team_roster_members
    ADD CONSTRAINT team_roster_members_roster_id_fkey FOREIGN KEY (roster_id) REFERENCES public.team_rosters(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.team_roster_members
    ADD CONSTRAINT team_roster_members_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.team_rosters
    ADD CONSTRAINT team_rosters_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.teams
    ADD CONSTRAINT teams_owner_id_fkey FOREIGN KEY (owner_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.tournament_announcements
    ADD CONSTRAINT tournament_announcements_sender_id_fkey FOREIGN KEY (sender_id) REFERENCES public.profiles(id);



ALTER TABLE ONLY public.tournament_announcements
    ADD CONSTRAINT tournament_announcements_tournament_id_fkey FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.tournament_bans
    ADD CONSTRAINT tournament_bans_banned_by_fkey FOREIGN KEY (banned_by) REFERENCES public.profiles(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.tournament_bans
    ADD CONSTRAINT tournament_bans_lifted_by_fkey FOREIGN KEY (lifted_by) REFERENCES public.profiles(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.tournament_bans
    ADD CONSTRAINT tournament_bans_participant_id_fkey FOREIGN KEY (participant_id) REFERENCES public.tournament_participants(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.tournament_bans
    ADD CONSTRAINT tournament_bans_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.tournament_bans
    ADD CONSTRAINT tournament_bans_tournament_id_fkey FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.tournament_bans
    ADD CONSTRAINT tournament_bans_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.tournament_disputes
    ADD CONSTRAINT tournament_disputes_assigned_to_user_id_fkey FOREIGN KEY (assigned_to_user_id) REFERENCES auth.users(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.tournament_disputes
    ADD CONSTRAINT tournament_disputes_match_id_fkey FOREIGN KEY (match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.tournament_disputes
    ADD CONSTRAINT tournament_disputes_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.tournament_disputes
    ADD CONSTRAINT tournament_disputes_tournament_id_fkey FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.tournament_invitations
    ADD CONSTRAINT tournament_invitations_redeemed_by_fkey FOREIGN KEY (redeemed_by) REFERENCES public.profiles(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.tournament_invitations
    ADD CONSTRAINT tournament_invitations_redeemed_participant_id_fkey FOREIGN KEY (redeemed_participant_id) REFERENCES public.tournament_participants(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.tournament_invitations
    ADD CONSTRAINT tournament_invitations_redeemed_team_id_fkey FOREIGN KEY (redeemed_team_id) REFERENCES public.teams(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.tournament_invitations
    ADD CONSTRAINT tournament_invitations_tournament_id_fkey FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.tournament_map_pools
    ADD CONSTRAINT tournament_map_pools_map_id_fkey1 FOREIGN KEY (map_id) REFERENCES public.game_maps(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.tournament_map_pools
    ADD CONSTRAINT tournament_map_pools_tournament_id_fkey1 FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.tournament_match_results
    ADD CONSTRAINT tournament_match_results_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.tournament_match_results
    ADD CONSTRAINT tournament_match_results_tournament_id_fkey FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.tournament_participants
    ADD CONSTRAINT tournament_participants_roster_id_fkey FOREIGN KEY (roster_id) REFERENCES public.team_rosters(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.tournament_participants
    ADD CONSTRAINT tournament_participants_team_captain_id_fkey FOREIGN KEY (team_captain_id) REFERENCES public.profiles(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.tournament_participants
    ADD CONSTRAINT tournament_participants_team_id_fkey FOREIGN KEY (team_id) REFERENCES public.teams(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.tournament_participants
    ADD CONSTRAINT tournament_participants_tournament_id_fkey FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.tournament_participants
    ADD CONSTRAINT tournament_participants_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.tournament_participants
    ADD CONSTRAINT tournament_participants_verified_by_fkey FOREIGN KEY (verified_by) REFERENCES public.profiles(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.tournament_stages
    ADD CONSTRAINT tournament_stages_tournament_id_fkey FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.tournaments
    ADD CONSTRAINT tournaments_approved_by_fkey FOREIGN KEY (approved_by) REFERENCES public.profiles(id);



ALTER TABLE ONLY public.tournaments
    ADD CONSTRAINT tournaments_organization_id_fkey FOREIGN KEY (organization_id) REFERENCES public.organizations(id);



ALTER TABLE ONLY public.tournaments
    ADD CONSTRAINT tournaments_organizer_id_fkey FOREIGN KEY (organizer_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.tournaments
    ADD CONSTRAINT tournaments_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.tournaments
    ADD CONSTRAINT tournaments_winner_id_fkey FOREIGN KEY (winner_id) REFERENCES public.teams(id);



ALTER TABLE ONLY public.user_roles
    ADD CONSTRAINT user_roles_assigned_by_fkey FOREIGN KEY (assigned_by) REFERENCES public.profiles(id);



ALTER TABLE ONLY public.user_roles
    ADD CONSTRAINT user_roles_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_announcements
    ADD CONSTRAINT venue_announcements_created_by_fkey FOREIGN KEY (created_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.venue_announcements
    ADD CONSTRAINT venue_announcements_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_availability_snapshot
    ADD CONSTRAINT venue_availability_snapshot_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_availability
    ADD CONSTRAINT venue_availability_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_billing_config
    ADD CONSTRAINT venue_billing_config_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_bookings
    ADD CONSTRAINT venue_bookings_cancelled_by_fkey FOREIGN KEY (cancelled_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.venue_bookings
    ADD CONSTRAINT venue_bookings_member_id_fkey FOREIGN KEY (member_id) REFERENCES public.members(id);



ALTER TABLE ONLY public.venue_bookings
    ADD CONSTRAINT venue_bookings_original_booking_id_fkey FOREIGN KEY (original_booking_id) REFERENCES public.venue_bookings(id);



ALTER TABLE ONLY public.venue_bookings
    ADD CONSTRAINT venue_bookings_session_id_fkey FOREIGN KEY (session_id) REFERENCES public.venue_sessions(id);



ALTER TABLE ONLY public.venue_bookings
    ADD CONSTRAINT venue_bookings_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_bookings
    ADD CONSTRAINT venue_bookings_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_bookings
    ADD CONSTRAINT venue_bookings_zone_id_fkey FOREIGN KEY (zone_id) REFERENCES public.zones(id);



ALTER TABLE ONLY public.venue_combos
    ADD CONSTRAINT venue_combos_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_impressions
    ADD CONSTRAINT venue_impressions_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.venue_impressions
    ADD CONSTRAINT venue_impressions_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_live_status
    ADD CONSTRAINT venue_live_status_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_loyalty_config
    ADD CONSTRAINT venue_loyalty_config_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_menu_items
    ADD CONSTRAINT venue_menu_items_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_packages
    ADD CONSTRAINT venue_packages_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_receipts
    ADD CONSTRAINT venue_receipts_issued_by_fkey FOREIGN KEY (issued_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.venue_receipts
    ADD CONSTRAINT venue_receipts_session_id_fkey FOREIGN KEY (session_id) REFERENCES public.venue_sessions(id);



ALTER TABLE ONLY public.venue_receipts
    ADD CONSTRAINT venue_receipts_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_reviews
    ADD CONSTRAINT venue_reviews_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_reviews
    ADD CONSTRAINT venue_reviews_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_session_events
    ADD CONSTRAINT venue_session_events_session_id_fkey FOREIGN KEY (session_id) REFERENCES public.venue_sessions(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_sessions
    ADD CONSTRAINT venue_sessions_booking_id_fkey FOREIGN KEY (booking_id) REFERENCES public.venue_bookings(id);



ALTER TABLE ONLY public.venue_sessions
    ADD CONSTRAINT venue_sessions_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users(id);



ALTER TABLE ONLY public.venue_sessions
    ADD CONSTRAINT venue_sessions_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_staff
    ADD CONSTRAINT venue_staff_invited_by_fkey FOREIGN KEY (invited_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.venue_staff_invites
    ADD CONSTRAINT venue_staff_invites_invited_by_fkey FOREIGN KEY (invited_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.venue_staff_invites
    ADD CONSTRAINT venue_staff_invites_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_staff
    ADD CONSTRAINT venue_staff_user_id_fkey FOREIGN KEY (user_id) REFERENCES auth.users(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_staff
    ADD CONSTRAINT venue_staff_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_stations
    ADD CONSTRAINT venue_stations_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.venue_stations
    ADD CONSTRAINT venue_stations_zone_id_fkey FOREIGN KEY (zone_id) REFERENCES public.zones(id) ON DELETE SET NULL;



ALTER TABLE ONLY public.venues
    ADD CONSTRAINT venues_organization_id_fkey FOREIGN KEY (organization_id) REFERENCES public.organizations(id);



ALTER TABLE ONLY public.venues
    ADD CONSTRAINT venues_reviewed_by_fkey FOREIGN KEY (reviewed_by) REFERENCES public.profiles(id);



ALTER TABLE ONLY public.verification_requests
    ADD CONSTRAINT verification_requests_reviewed_by_fkey FOREIGN KEY (reviewed_by) REFERENCES public.profiles(id);



ALTER TABLE ONLY public.verification_requests
    ADD CONSTRAINT verification_requests_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.verified_roles
    ADD CONSTRAINT verified_roles_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.verified_roles
    ADD CONSTRAINT verified_roles_verification_request_id_fkey FOREIGN KEY (verification_request_id) REFERENCES public.verification_requests(id);



ALTER TABLE ONLY public.verified_roles
    ADD CONSTRAINT verified_roles_verified_by_fkey FOREIGN KEY (verified_by) REFERENCES public.profiles(id);



ALTER TABLE ONLY public.walk_in_queue
    ADD CONSTRAINT walk_in_queue_member_id_fkey FOREIGN KEY (member_id) REFERENCES public.members(id);



ALTER TABLE ONLY public.walk_in_queue
    ADD CONSTRAINT walk_in_queue_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



ALTER TABLE ONLY public.walk_in_queue
    ADD CONSTRAINT walk_in_queue_zone_id_fkey FOREIGN KEY (zone_id) REFERENCES public.zones(id);



ALTER TABLE ONLY public.wallet_transactions
    ADD CONSTRAINT wallet_transactions_created_by_fkey FOREIGN KEY (created_by) REFERENCES auth.users(id);



ALTER TABLE ONLY public.wallet_transactions
    ADD CONSTRAINT wallet_transactions_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id);



ALTER TABLE ONLY public.wallet_transactions
    ADD CONSTRAINT wallet_transactions_wallet_id_fkey FOREIGN KEY (wallet_id) REFERENCES public.customer_wallets(id) ON DELETE RESTRICT;



ALTER TABLE ONLY public.zones
    ADD CONSTRAINT zones_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON DELETE CASCADE;



CREATE POLICY "Admins bypass assignment RLS" ON public.staff_tournament_assignments TO authenticated USING (public.is_admin()) WITH CHECK (public.is_admin());



CREATE POLICY "Admins bypass audit log RLS" ON public.staff_audit_log TO authenticated USING (public.is_admin()) WITH CHECK (public.is_admin());



CREATE POLICY "Admins bypass org staff RLS" ON public.organization_staff TO authenticated USING (public.is_admin()) WITH CHECK (public.is_admin());



CREATE POLICY "Admins can delete partner applications" ON public.partner_applications FOR DELETE USING ((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))));



CREATE POLICY "Admins can manage admin permissions" ON public.admin_permissions USING ((EXISTS ( SELECT 1
   FROM public.profiles p
  WHERE ((p.id = ( SELECT auth.uid() AS uid)) AND (p.is_admin = true)))));



CREATE POLICY "Admins can manage admin role permissions" ON public.admin_role_permissions USING ((EXISTS ( SELECT 1
   FROM public.profiles p
  WHERE ((p.id = ( SELECT auth.uid() AS uid)) AND (p.is_admin = true)))));



CREATE POLICY "Admins can manage admin roles" ON public.admin_roles USING ((EXISTS ( SELECT 1
   FROM public.profiles p
  WHERE ((p.id = ( SELECT auth.uid() AS uid)) AND (p.is_admin = true)))));



CREATE POLICY "Admins can manage admin user roles" ON public.admin_user_roles USING ((EXISTS ( SELECT 1
   FROM public.profiles p
  WHERE ((p.id = ( SELECT auth.uid() AS uid)) AND (p.is_admin = true)))));



CREATE POLICY "Admins can update any venue" ON public.venues FOR UPDATE USING ((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = auth.uid()) AND ((profiles.is_admin = true) OR ('venue_admin'::text = ANY (profiles.admin_roles)))))));



CREATE POLICY "Admins can update partner applications" ON public.partner_applications FOR UPDATE USING ((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))));



CREATE POLICY "Admins can update system settings" ON public.system_settings FOR UPDATE USING ((EXISTS ( SELECT 1
   FROM public.profiles p
  WHERE ((p.id = ( SELECT auth.uid() AS uid)) AND (p.is_admin = true))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.profiles p
  WHERE ((p.id = ( SELECT auth.uid() AS uid)) AND (p.is_admin = true)))));



CREATE POLICY "Admins can view audit logs" ON public.audit_logs FOR SELECT TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = auth.uid()) AND (profiles.is_admin = true)))));



CREATE POLICY "Admins can view partner applications" ON public.partner_applications FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))));



CREATE POLICY "Admins can view system settings" ON public.system_settings FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.profiles p
  WHERE ((p.id = ( SELECT auth.uid() AS uid)) AND (p.is_admin = true)))));



CREATE POLICY "Admins manage all licenses" ON public.licenses USING ((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = auth.uid()) AND ((profiles.is_admin = true) OR ('venue_admin'::text = ANY (profiles.admin_roles)))))));



CREATE POLICY "Allow partners to view their own analytics" ON public.sponsor_impressions FOR SELECT TO authenticated USING (((sponsor_id IN ( SELECT sponsor_accounts.sponsor_id
   FROM public.sponsor_accounts
  WHERE (sponsor_accounts.user_id = auth.uid()))) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = auth.uid()) AND (profiles.is_admin = true))))));



CREATE POLICY "Allow staff invite notifications" ON public.notifications FOR INSERT TO authenticated WITH CHECK ((type = 'staff_invite'::public.notification_type));



CREATE POLICY "Anyone can log impressions" ON public.venue_impressions FOR INSERT WITH CHECK (true);



CREATE POLICY "Anyone can read live status" ON public.venue_live_status FOR SELECT USING (true);



CREATE POLICY "Authenticated users can create reviews" ON public.venue_reviews FOR INSERT WITH CHECK ((( SELECT auth.uid() AS uid) = user_id));



CREATE POLICY "Authenticated users can insert audit logs" ON public.audit_logs FOR INSERT TO authenticated WITH CHECK ((admin_id = auth.uid()));



CREATE POLICY "Authenticated users can insert audit logs" ON public.staff_audit_log FOR INSERT TO authenticated WITH CHECK ((actor_id = auth.uid()));



CREATE POLICY "Authorized staff can create announcements" ON public.tournament_announcements FOR INSERT WITH CHECK ((EXISTS ( SELECT 1
   FROM (public.organization_staff os
     JOIN public.tournaments t ON ((t.organization_id = os.organization_id)))
  WHERE ((t.id = tournament_announcements.tournament_id) AND (os.user_id = auth.uid()) AND (os.status = 'active'::text) AND ((os.role = ANY (ARRAY['owner'::text, 'admin'::text])) OR (EXISTS ( SELECT 1
           FROM public.staff_tournament_assignments sta
          WHERE ((sta.organization_staff_id = os.id) AND (sta.tournament_id = t.id)))))))));



CREATE POLICY "Authorized staff can delete announcements" ON public.tournament_announcements FOR DELETE USING (((auth.uid() = sender_id) OR (EXISTS ( SELECT 1
   FROM (public.organization_staff os
     JOIN public.tournaments t ON ((t.organization_id = os.organization_id)))
  WHERE ((t.id = tournament_announcements.tournament_id) AND (os.user_id = auth.uid()) AND (os.status = 'active'::text) AND (os.role = ANY (ARRAY['owner'::text, 'admin'::text])))))));



CREATE POLICY "Org admins can manage staff" ON public.organization_staff TO authenticated USING (public.is_org_admin(organization_id)) WITH CHECK (public.is_org_admin(organization_id));



CREATE POLICY "Org admins can view audit logs" ON public.staff_audit_log FOR SELECT TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.organization_staff
  WHERE ((organization_staff.organization_id = staff_audit_log.organization_id) AND (organization_staff.user_id = auth.uid()) AND (organization_staff.role = 'admin'::text) AND (organization_staff.status = 'active'::text)))));



CREATE POLICY "Org owners can manage staff" ON public.organization_staff TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.organizations
  WHERE ((organizations.id = organization_staff.organization_id) AND (organizations.owner_id = auth.uid()))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.organizations
  WHERE ((organizations.id = organization_staff.organization_id) AND (organizations.owner_id = auth.uid())))));



CREATE POLICY "Org owners can view audit logs" ON public.staff_audit_log FOR SELECT TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.organizations
  WHERE ((organizations.id = staff_audit_log.organization_id) AND (organizations.owner_id = auth.uid())))));



CREATE POLICY "Org owners manage tournament assignments" ON public.staff_tournament_assignments TO authenticated USING ((EXISTS ( SELECT 1
   FROM (public.organization_staff os
     JOIN public.organizations o ON ((o.id = os.organization_id)))
  WHERE ((os.id = staff_tournament_assignments.organization_staff_id) AND (o.owner_id = auth.uid()))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM (public.organization_staff os
     JOIN public.organizations o ON ((o.id = os.organization_id)))
  WHERE ((os.id = staff_tournament_assignments.organization_staff_id) AND (o.owner_id = auth.uid())))));



CREATE POLICY "Organization owners can delete media" ON public.organization_media FOR DELETE USING ((( SELECT auth.uid() AS uid) IN ( SELECT organizations.owner_id
   FROM public.organizations
  WHERE (organizations.id = organization_media.organization_id))));



CREATE POLICY "Organization owners can insert media" ON public.organization_media FOR INSERT WITH CHECK ((( SELECT auth.uid() AS uid) IN ( SELECT organizations.owner_id
   FROM public.organizations
  WHERE (organizations.id = organization_media.organization_id))));



CREATE POLICY "Organizers can resolve disputes" ON public.match_disputes FOR UPDATE USING (((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = auth.uid()) AND ((profiles.is_admin = true) OR (profiles.role = 'admin'::public.app_role))))) OR (EXISTS ( SELECT 1
   FROM ((public.brkt_matches m
     JOIN public.brkt_versions v ON ((m.version_id = v.id)))
     JOIN public.tournaments t ON ((v.tournament_id = t.id)))
  WHERE ((m.id = match_disputes.match_id) AND (t.organizer_id = auth.uid()))))));



CREATE POLICY "Owners can update their own venues" ON public.venues FOR UPDATE USING (((auth.uid() IS NOT NULL) AND (owner_id = auth.uid()))) WITH CHECK ((status = ANY (ARRAY['draft'::text, 'pending_review'::text])));



CREATE POLICY "Owners see own venue impressions" ON public.venue_impressions FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venues v
  WHERE ((v.id = venue_impressions.venue_id) AND (v.owner_id = auth.uid())))));



CREATE POLICY "Owners update own live status" ON public.venue_live_status USING ((EXISTS ( SELECT 1
   FROM public.venues v
  WHERE ((v.id = venue_live_status.venue_id) AND (v.owner_id = auth.uid())))));



CREATE POLICY "Participants and organizers can view disputes" ON public.match_disputes FOR SELECT USING (true);



CREATE POLICY "Participants can file disputes" ON public.match_disputes FOR INSERT WITH CHECK ((disputed_by_user_id = auth.uid()));



CREATE POLICY "Participants can view announcements" ON public.tournament_announcements FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.tournament_participants
  WHERE ((tournament_participants.tournament_id = tournament_announcements.tournament_id) AND (tournament_participants.user_id = auth.uid())))));



CREATE POLICY "Public media are viewable by everyone" ON public.organization_media FOR SELECT USING (true);



CREATE POLICY "Public read branding settings" ON public.system_settings FOR SELECT USING ((key = ANY (ARRAY['platform_logo_url'::text, 'platform_icon_url'::text])));



CREATE POLICY "Published venues are viewable by everyone" ON public.venues FOR SELECT USING (((status = 'published'::text) OR ((auth.uid() IS NOT NULL) AND (owner_id = auth.uid())) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = auth.uid()) AND ((profiles.is_admin = true) OR ('venue_admin'::text = ANY (profiles.admin_roles))))))));



CREATE POLICY "Reviews are viewable by everyone" ON public.venue_reviews FOR SELECT USING (true);



CREATE POLICY "Staff can view announcements" ON public.tournament_announcements FOR SELECT USING ((EXISTS ( SELECT 1
   FROM (public.organization_staff os
     JOIN public.tournaments t ON ((t.organization_id = os.organization_id)))
  WHERE ((t.id = tournament_announcements.tournament_id) AND (os.user_id = auth.uid()) AND (os.status = 'active'::text)))));



CREATE POLICY "Staff can view other staff in same org" ON public.organization_staff FOR SELECT USING (public.is_org_active_staff(organization_id));



CREATE POLICY "Staff can view own assignments" ON public.staff_tournament_assignments FOR SELECT USING (public.is_org_staff_user(organization_staff_id));



CREATE POLICY "Users can create bookings" ON public.venue_bookings FOR INSERT WITH CHECK ((( SELECT auth.uid() AS uid) = user_id));



CREATE POLICY "Users can delete own reviews" ON public.venue_reviews FOR DELETE USING ((( SELECT auth.uid() AS uid) = user_id));



CREATE POLICY "Users can respond to invites" ON public.organization_staff FOR UPDATE TO authenticated USING ((user_id = auth.uid())) WITH CHECK ((user_id = auth.uid()));



CREATE POLICY "Users can update own reviews" ON public.venue_reviews FOR UPDATE USING ((( SELECT auth.uid() AS uid) = user_id));



CREATE POLICY "Users can update their own bookings" ON public.venue_bookings FOR UPDATE USING ((( SELECT auth.uid() AS uid) = user_id));



CREATE POLICY "Users can update their own sponsor account" ON public.sponsor_accounts FOR UPDATE USING ((auth.uid() = user_id));



CREATE POLICY "Users can view own staff records" ON public.organization_staff FOR SELECT TO authenticated USING ((user_id = auth.uid()));



CREATE POLICY "Users can view their own bookings" ON public.venue_bookings FOR SELECT USING ((( SELECT auth.uid() AS uid) = user_id));



CREATE POLICY "Users see own licenses" ON public.licenses FOR SELECT USING ((user_id = auth.uid()));



ALTER TABLE public.account_security_state ENABLE ROW LEVEL SECURITY;


CREATE POLICY account_security_state_service_role_all ON public.account_security_state TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.activity_log ENABLE ROW LEVEL SECURITY;


CREATE POLICY activity_log_owner_staff_insert ON public.activity_log FOR INSERT WITH CHECK (((EXISTS ( SELECT 1
   FROM public.venues
  WHERE ((venues.id = activity_log.venue_id) AND (venues.owner_id = auth.uid())))) OR (EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = activity_log.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text))))));



CREATE POLICY activity_log_owner_staff_select ON public.activity_log FOR SELECT USING (((EXISTS ( SELECT 1
   FROM public.venues
  WHERE ((venues.id = activity_log.venue_id) AND (venues.owner_id = auth.uid())))) OR (EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = activity_log.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text))))));



CREATE POLICY activity_log_service_role_all ON public.activity_log USING (true) WITH CHECK (true);



ALTER TABLE public.admin_alerts ENABLE ROW LEVEL SECURITY;


CREATE POLICY admin_alerts_admin_read ON public.admin_alerts FOR SELECT TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid()))));



CREATE POLICY admin_alerts_service_role_all ON public.admin_alerts TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.admin_dashboard_preferences ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.admin_ip_allowlist ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.admin_permissions ENABLE ROW LEVEL SECURITY;


CREATE POLICY admin_read_daily_stats ON public.daily_sponsor_stats FOR SELECT USING (true);



ALTER TABLE public.admin_role_permissions ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.admin_roles ENABLE ROW LEVEL SECURITY;


CREATE POLICY admin_roles_select_authenticated ON public.admin_roles FOR SELECT TO authenticated USING (true);



CREATE POLICY admin_roles_write_admin_only ON public.admin_roles TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = auth.uid()) AND (profiles.is_admin = true))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = auth.uid()) AND (profiles.is_admin = true)))));



ALTER TABLE public.admin_session_audit ENABLE ROW LEVEL SECURITY;


CREATE POLICY admin_session_audit_admin_read ON public.admin_session_audit FOR SELECT TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.profiles p
  WHERE ((p.id = auth.uid()) AND (p.is_admin = true)))));



CREATE POLICY admin_session_audit_self_insert ON public.admin_session_audit FOR INSERT TO authenticated WITH CHECK ((user_id = auth.uid()));



ALTER TABLE public.admin_user_roles ENABLE ROW LEVEL SECURITY;


CREATE POLICY admin_user_roles_admin_all ON public.admin_user_roles TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = auth.uid()) AND (profiles.is_admin = true))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = auth.uid()) AND (profiles.is_admin = true)))));



ALTER TABLE public.anomaly_events ENABLE ROW LEVEL SECURITY;


CREATE POLICY anomaly_events_admin_read ON public.anomaly_events FOR SELECT TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid()))));



CREATE POLICY anomaly_events_service_role_all ON public.anomaly_events TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.anomaly_rules ENABLE ROW LEVEL SECURITY;


CREATE POLICY anomaly_rules_admin_read ON public.anomaly_rules FOR SELECT TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid()))));



CREATE POLICY anomaly_rules_service_role_all ON public.anomaly_rules TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.audit_logs ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.balance_transactions ENABLE ROW LEVEL SECURITY;


CREATE POLICY balance_tx_own_select ON public.balance_transactions FOR SELECT USING ((auth.uid() = user_id));



CREATE POLICY balance_tx_service_all ON public.balance_transactions USING ((auth.role() = 'service_role'::text));



CREATE POLICY billing_config_owner_select ON public.venue_billing_config FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venues v
  WHERE ((v.id = venue_billing_config.venue_id) AND (v.owner_id = auth.uid())))));



CREATE POLICY billing_config_service_all ON public.venue_billing_config USING ((auth.role() = 'service_role'::text)) WITH CHECK ((auth.role() = 'service_role'::text));



ALTER TABLE public.booking_rules ENABLE ROW LEVEL SECURITY;


CREATE POLICY booking_rules_owner_select ON public.booking_rules FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venues
  WHERE ((venues.id = booking_rules.venue_id) AND (venues.owner_id = auth.uid())))));



CREATE POLICY booking_rules_service_all ON public.booking_rules USING ((current_setting('request.jwt.claim.role'::text, true) = 'service_role'::text));



CREATE POLICY booking_rules_staff_select ON public.booking_rules FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venue_staff
  WHERE ((venue_staff.venue_id = booking_rules.venue_id) AND (venue_staff.user_id = auth.uid()) AND (venue_staff.status = 'active'::text)))));



ALTER TABLE public.br_game_data ENABLE ROW LEVEL SECURITY;


CREATE POLICY br_game_data_insert ON public.br_game_data FOR INSERT TO authenticated WITH CHECK (true);



CREATE POLICY br_game_data_select ON public.br_game_data FOR SELECT TO authenticated USING (true);



CREATE POLICY br_game_data_update ON public.br_game_data FOR UPDATE TO authenticated USING (true) WITH CHECK (true);



ALTER TABLE public.br_games ENABLE ROW LEVEL SECURITY;


CREATE POLICY br_games_authenticated_select ON public.br_games FOR SELECT TO authenticated USING (true);



ALTER TABLE public.br_group_teams ENABLE ROW LEVEL SECURITY;


CREATE POLICY br_group_teams_authenticated_select ON public.br_group_teams FOR SELECT TO authenticated USING (true);



ALTER TABLE public.br_groups ENABLE ROW LEVEL SECURITY;


CREATE POLICY br_groups_authenticated_select ON public.br_groups FOR SELECT TO authenticated USING (true);



ALTER TABLE public.br_lobbies ENABLE ROW LEVEL SECURITY;


CREATE POLICY br_lobbies_authenticated_select ON public.br_lobbies FOR SELECT TO authenticated USING (true);



ALTER TABLE public.br_lobby_evidence ENABLE ROW LEVEL SECURITY;


CREATE POLICY br_lobby_evidence_service_role_all ON public.br_lobby_evidence TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.br_lobby_groups ENABLE ROW LEVEL SECURITY;


CREATE POLICY br_lobby_groups_authenticated_select ON public.br_lobby_groups FOR SELECT TO authenticated USING (true);



ALTER TABLE public.br_lobby_results ENABLE ROW LEVEL SECURITY;


CREATE POLICY br_round_evidence_authenticated_select ON public.br_lobby_evidence FOR SELECT TO authenticated USING (true);



CREATE POLICY br_round_evidence_service_role_all ON public.br_lobby_evidence TO service_role USING (true) WITH CHECK (true);



CREATE POLICY br_round_results_authenticated_select ON public.br_lobby_results FOR SELECT TO authenticated USING (true);



ALTER TABLE public.brkt_advancements ENABLE ROW LEVEL SECURITY;


CREATE POLICY brkt_advancements_delete_policy ON public.brkt_advancements FOR DELETE USING (((( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true))))));



CREATE POLICY brkt_advancements_enterprise_select ON public.brkt_advancements FOR SELECT TO authenticated USING (true);



CREATE POLICY brkt_advancements_insert_policy ON public.brkt_advancements FOR INSERT WITH CHECK (((auth.role() = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM (public.brkt_versions bv
     JOIN public.tournaments t ON ((t.id = bv.tournament_id)))
  WHERE ((bv.id = brkt_advancements.version_id) AND ((t.organizer_id = auth.uid()) OR (t.organization_id IN ( SELECT organizations.id
           FROM public.organizations
          WHERE (organizations.owner_id = auth.uid())))))))));



CREATE POLICY brkt_advancements_update_policy ON public.brkt_advancements FOR UPDATE USING (((( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true))))));



ALTER TABLE public.brkt_layout ENABLE ROW LEVEL SECURITY;


CREATE POLICY brkt_layout_delete_policy ON public.brkt_layout FOR DELETE USING (((( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true))))));



CREATE POLICY brkt_layout_insert_policy ON public.brkt_layout FOR INSERT WITH CHECK (((( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM ((public.brkt_versions bv
     JOIN public.tournament_stages ts ON ((ts.id = bv.stage_id)))
     JOIN public.tournaments t ON ((t.id = ts.tournament_id)))
  WHERE ((bv.id = brkt_layout.version_id) AND (t.organizer_id = ( SELECT auth.uid() AS uid)))))));



CREATE POLICY brkt_layout_select_policy ON public.brkt_layout FOR SELECT USING (true);



CREATE POLICY brkt_layout_update_policy ON public.brkt_layout FOR UPDATE USING (((( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true))))));



ALTER TABLE public.brkt_match_events ENABLE ROW LEVEL SECURITY;


CREATE POLICY brkt_match_events_delete_policy ON public.brkt_match_events FOR DELETE USING (((( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true))))));



CREATE POLICY brkt_match_events_insert_policy ON public.brkt_match_events FOR INSERT WITH CHECK (((( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM (((public.brkt_matches bm
     JOIN public.brkt_versions bv ON ((bv.id = bm.version_id)))
     JOIN public.tournament_stages ts ON ((ts.id = bv.stage_id)))
     JOIN public.tournaments t ON ((t.id = ts.tournament_id)))
  WHERE ((bm.id = brkt_match_events.match_id) AND (t.organizer_id = ( SELECT auth.uid() AS uid)))))));



CREATE POLICY brkt_match_events_select_policy ON public.brkt_match_events FOR SELECT USING (true);



CREATE POLICY brkt_match_events_update_policy ON public.brkt_match_events FOR UPDATE USING (((( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true))))));



ALTER TABLE public.brkt_match_games ENABLE ROW LEVEL SECURITY;


CREATE POLICY brkt_match_games_all_policy ON public.brkt_match_games USING (((auth.role() = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM ((public.brkt_matches bm
     JOIN public.brkt_versions bv ON ((bv.id = bm.version_id)))
     JOIN public.tournaments t ON ((t.id = bv.tournament_id)))
  WHERE ((bm.id = brkt_match_games.match_id) AND ((t.organizer_id = auth.uid()) OR (t.organization_id IN ( SELECT organizations.id
           FROM public.organizations
          WHERE (organizations.owner_id = auth.uid())))))))));



CREATE POLICY brkt_match_games_select_policy ON public.brkt_match_games FOR SELECT USING (true);



ALTER TABLE public.brkt_matches ENABLE ROW LEVEL SECURITY;


CREATE POLICY brkt_matches_delete_policy ON public.brkt_matches FOR DELETE USING (((auth.role() = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = auth.uid()) AND (profiles.is_admin = true)))) OR (EXISTS ( SELECT 1
   FROM (public.brkt_versions bv
     JOIN public.tournaments t ON ((t.id = bv.tournament_id)))
  WHERE ((bv.id = brkt_matches.version_id) AND ((t.organizer_id = auth.uid()) OR (t.organization_id IN ( SELECT organizations.id
           FROM public.organizations
          WHERE (organizations.owner_id = auth.uid())))))))));



CREATE POLICY brkt_matches_enterprise_select ON public.brkt_matches FOR SELECT TO authenticated USING (true);



CREATE POLICY brkt_matches_insert_policy ON public.brkt_matches FOR INSERT WITH CHECK (((auth.role() = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = auth.uid()) AND (profiles.is_admin = true)))) OR (EXISTS ( SELECT 1
   FROM (public.brkt_versions bv
     JOIN public.tournaments t ON ((t.id = bv.tournament_id)))
  WHERE ((bv.id = brkt_matches.version_id) AND ((t.organizer_id = auth.uid()) OR (t.organization_id IN ( SELECT organizations.id
           FROM public.organizations
          WHERE (organizations.owner_id = auth.uid())))))))));



CREATE POLICY brkt_matches_public_select ON public.brkt_matches FOR SELECT USING (true);



CREATE POLICY brkt_matches_update_policy ON public.brkt_matches FOR UPDATE USING (((auth.role() = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = auth.uid()) AND (profiles.is_admin = true)))) OR (EXISTS ( SELECT 1
   FROM (public.brkt_versions bv
     JOIN public.tournaments t ON ((t.id = bv.tournament_id)))
  WHERE ((bv.id = brkt_matches.version_id) AND ((t.organizer_id = auth.uid()) OR (t.organization_id IN ( SELECT organizations.id
           FROM public.organizations
          WHERE (organizations.owner_id = auth.uid())))))))));



ALTER TABLE public.brkt_versions ENABLE ROW LEVEL SECURITY;


CREATE POLICY brkt_versions_authenticated_select ON public.brkt_versions FOR SELECT TO authenticated USING (((status <> 'archived'::text) OR public.is_admin()));



CREATE POLICY brkt_versions_delete_policy ON public.brkt_versions FOR DELETE USING ((EXISTS ( SELECT 1
   FROM public.tournaments t
  WHERE ((t.id = brkt_versions.tournament_id) AND ((t.organizer_id = auth.uid()) OR (t.organization_id IN ( SELECT organizations.id
           FROM public.organizations
          WHERE (organizations.owner_id = auth.uid()))))))));



CREATE POLICY brkt_versions_enterprise_insert ON public.brkt_versions FOR INSERT TO authenticated WITH CHECK ((public.is_admin() OR (EXISTS ( SELECT 1
   FROM public.tournaments t
  WHERE ((t.id = brkt_versions.tournament_id) AND ((t.organizer_id = auth.uid()) OR (t.organization_id IN ( SELECT organizations.id
           FROM public.organizations
          WHERE (organizations.owner_id = auth.uid())))))))));



CREATE POLICY brkt_versions_enterprise_update ON public.brkt_versions FOR UPDATE TO authenticated USING ((public.is_admin() OR (EXISTS ( SELECT 1
   FROM public.tournaments t
  WHERE ((t.id = brkt_versions.tournament_id) AND ((t.organizer_id = auth.uid()) OR (t.organization_id IN ( SELECT organizations.id
           FROM public.organizations
          WHERE (organizations.owner_id = auth.uid())))))))));



ALTER TABLE public.broadcast_deliveries ENABLE ROW LEVEL SECURITY;


CREATE POLICY broadcast_deliveries_service ON public.broadcast_deliveries TO service_role USING (true) WITH CHECK (true);



CREATE POLICY broadcast_deliveries_user_read ON public.broadcast_deliveries FOR SELECT TO authenticated USING ((user_id = auth.uid()));



CREATE POLICY broadcast_deliveries_user_update ON public.broadcast_deliveries FOR UPDATE TO authenticated USING ((user_id = auth.uid())) WITH CHECK ((user_id = auth.uid()));



ALTER TABLE public.broadcast_licenses ENABLE ROW LEVEL SECURITY;


CREATE POLICY broadcast_licenses_own ON public.broadcast_licenses USING ((auth.uid() = user_id));



CREATE POLICY broadcast_licenses_service_all ON public.broadcast_licenses USING ((auth.role() = 'service_role'::text));



ALTER TABLE public.broadcast_overlay_layouts ENABLE ROW LEVEL SECURITY;


CREATE POLICY broadcast_overlay_layouts_own ON public.broadcast_overlay_layouts USING ((auth.uid() = user_id));



CREATE POLICY broadcast_overlay_layouts_public_read ON public.broadcast_overlay_layouts FOR SELECT USING (((is_template = true) OR (is_public = true)));



CREATE POLICY broadcast_overlay_layouts_service_all ON public.broadcast_overlay_layouts USING ((auth.role() = 'service_role'::text));



ALTER TABLE public.broadcast_templates ENABLE ROW LEVEL SECURITY;


CREATE POLICY broadcast_templates_service ON public.broadcast_templates TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.broadcast_themes ENABLE ROW LEVEL SECURITY;


CREATE POLICY broadcast_themes_own ON public.broadcast_themes USING ((auth.uid() = owner_id));



CREATE POLICY broadcast_themes_service_all ON public.broadcast_themes USING ((auth.role() = 'service_role'::text));



ALTER TABLE public.broadcasts ENABLE ROW LEVEL SECURITY;


CREATE POLICY broadcasts_service ON public.broadcasts TO service_role USING (true) WITH CHECK (true);



CREATE POLICY checkins_captain_insert ON public.match_checkins FOR INSERT WITH CHECK ((EXISTS ( SELECT 1
   FROM public.team_members tm
  WHERE ((tm.team_id = match_checkins.team_id) AND (tm.user_id = ( SELECT auth.uid() AS uid)) AND (tm.role = 'captain'::public.team_member_role)))));



CREATE POLICY checkins_public_select ON public.match_checkins FOR SELECT USING (true);



ALTER TABLE public.consent_records ENABLE ROW LEVEL SECURITY;


CREATE POLICY consent_records_admin_select ON public.consent_records FOR SELECT TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid()))));



CREATE POLICY consent_records_user_insert ON public.consent_records FOR INSERT TO authenticated WITH CHECK ((auth.uid() = user_id));



CREATE POLICY consent_records_user_select ON public.consent_records FOR SELECT TO authenticated USING ((auth.uid() = user_id));



ALTER TABLE public.customer_wallets ENABLE ROW LEVEL SECURITY;


CREATE POLICY customer_wallets_own_select ON public.customer_wallets FOR SELECT USING ((auth.uid() = user_id));



CREATE POLICY customer_wallets_service_all ON public.customer_wallets USING ((auth.role() = 'service_role'::text));



CREATE POLICY customer_wallets_staff_insert ON public.customer_wallets FOR INSERT WITH CHECK ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = customer_wallets.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



CREATE POLICY customer_wallets_staff_select ON public.customer_wallets FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = customer_wallets.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



CREATE POLICY customer_wallets_staff_update ON public.customer_wallets FOR UPDATE USING ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = customer_wallets.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = customer_wallets.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



ALTER TABLE public.daily_sponsor_stats ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.daily_stats ENABLE ROW LEVEL SECURITY;


CREATE POLICY daily_stats_owner_staff_select ON public.daily_stats FOR SELECT USING (((EXISTS ( SELECT 1
   FROM public.venues
  WHERE ((venues.id = daily_stats.venue_id) AND (venues.owner_id = auth.uid())))) OR (EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = daily_stats.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text))))));



CREATE POLICY daily_stats_service_role_all ON public.daily_stats USING (true) WITH CHECK (true);



CREATE POLICY dashboard_prefs_insert_admin ON public.admin_dashboard_preferences FOR INSERT WITH CHECK (((auth.uid() = user_id) AND (EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid())))));



CREATE POLICY dashboard_prefs_read_own ON public.admin_dashboard_preferences FOR SELECT USING ((auth.uid() = user_id));



CREATE POLICY dashboard_prefs_update_admin ON public.admin_dashboard_preferences FOR UPDATE USING (((auth.uid() = user_id) AND (EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid()))))) WITH CHECK ((auth.uid() = user_id));



CREATE POLICY dc_insert_own_or_organizer ON public.dispute_comments FOR INSERT WITH CHECK (((user_id = auth.uid()) AND ((EXISTS ( SELECT 1
   FROM public.tournament_disputes td
  WHERE ((td.id = dispute_comments.dispute_id) AND (td.raised_by_user_id = auth.uid())))) OR (EXISTS ( SELECT 1
   FROM ((public.tournament_disputes td
     JOIN public.brkt_matches bm ON ((bm.id = td.match_id)))
     JOIN public.team_members tm ON (((tm.team_id = bm.team1_id) OR (tm.team_id = bm.team2_id))))
  WHERE ((td.id = dispute_comments.dispute_id) AND (tm.user_id = auth.uid()) AND (tm.is_active = true)))) OR (EXISTS ( SELECT 1
   FROM (public.tournament_disputes td
     JOIN public.tournaments t ON ((t.id = td.tournament_id)))
  WHERE ((td.id = dispute_comments.dispute_id) AND ((t.organizer_id = auth.uid()) OR (td.assigned_to_user_id = auth.uid()))))) OR (EXISTS ( SELECT 1
   FROM ((public.tournament_disputes td
     JOIN public.organization_staff os ON ((os.organization_id = ( SELECT tournaments.organization_id
           FROM public.tournaments
          WHERE (tournaments.id = td.tournament_id)))))
     JOIN public.staff_tournament_assignments sta ON (((sta.organization_staff_id = os.id) AND (sta.tournament_id = td.tournament_id))))
  WHERE ((td.id = dispute_comments.dispute_id) AND (os.user_id = auth.uid()) AND (os.status = 'active'::text) AND ('disputes:assist'::text = ANY (os.permissions))))) OR (EXISTS ( SELECT 1
   FROM public.profiles p
  WHERE ((p.id = auth.uid()) AND (('moderator'::text = ANY (p.admin_roles)) OR ('ops_admin'::text = ANY (p.admin_roles)))))) OR (EXISTS ( SELECT 1
   FROM (public.admin_user_roles aur
     JOIN public.admin_roles ar ON ((ar.id = aur.role_id)))
  WHERE ((aur.user_id = auth.uid()) AND (lower(ar.name) = ANY (ARRAY['moderator'::text, 'ops_admin'::text]))))) OR (( SELECT auth.role() AS role) = 'service_role'::text))));



CREATE POLICY dc_select_own_or_dispute ON public.dispute_comments FOR SELECT USING (((user_id = auth.uid()) OR ((NOT is_internal) AND (EXISTS ( SELECT 1
   FROM ((public.tournament_disputes td
     JOIN public.brkt_matches bm ON ((bm.id = td.match_id)))
     JOIN public.team_members tm ON (((tm.team_id = bm.team1_id) OR (tm.team_id = bm.team2_id))))
  WHERE ((td.id = dispute_comments.dispute_id) AND (tm.user_id = auth.uid()) AND (tm.is_active = true))))) OR (EXISTS ( SELECT 1
   FROM (public.tournament_disputes td
     JOIN public.tournaments t ON ((t.id = td.tournament_id)))
  WHERE ((td.id = dispute_comments.dispute_id) AND ((t.organizer_id = auth.uid()) OR (td.assigned_to_user_id = auth.uid()))))) OR (EXISTS ( SELECT 1
   FROM ((public.tournament_disputes td
     JOIN public.organization_staff os ON ((os.organization_id = ( SELECT tournaments.organization_id
           FROM public.tournaments
          WHERE (tournaments.id = td.tournament_id)))))
     JOIN public.staff_tournament_assignments sta ON (((sta.organization_staff_id = os.id) AND (sta.tournament_id = td.tournament_id))))
  WHERE ((td.id = dispute_comments.dispute_id) AND (os.user_id = auth.uid()) AND (os.status = 'active'::text) AND ('disputes:assist'::text = ANY (os.permissions))))) OR (EXISTS ( SELECT 1
   FROM public.profiles p
  WHERE ((p.id = auth.uid()) AND (('moderator'::text = ANY (p.admin_roles)) OR ('ops_admin'::text = ANY (p.admin_roles)))))) OR (EXISTS ( SELECT 1
   FROM (public.admin_user_roles aur
     JOIN public.admin_roles ar ON ((ar.id = aur.role_id)))
  WHERE ((aur.user_id = auth.uid()) AND (lower(ar.name) = ANY (ARRAY['moderator'::text, 'ops_admin'::text]))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.dispute_comments ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.disputes ENABLE ROW LEVEL SECURITY;


CREATE POLICY disputes_admin_all ON public.disputes TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.profiles p
  WHERE ((p.id = auth.uid()) AND (p.is_admin = true)))));



CREATE POLICY disputes_authenticated_insert ON public.disputes FOR INSERT TO authenticated WITH CHECK ((reporter_id = auth.uid()));



CREATE POLICY disputes_reporter_read ON public.disputes FOR SELECT TO authenticated USING ((reporter_id = auth.uid()));



ALTER TABLE public.feature_flag_overrides ENABLE ROW LEVEL SECURITY;


CREATE POLICY feature_flag_overrides_service ON public.feature_flag_overrides TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.feature_flag_rules ENABLE ROW LEVEL SECURITY;


CREATE POLICY feature_flag_rules_service ON public.feature_flag_rules TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.feature_flags ENABLE ROW LEVEL SECURITY;


CREATE POLICY feature_flags_read ON public.feature_flags FOR SELECT TO authenticated USING ((is_enabled = true));



CREATE POLICY feature_flags_service ON public.feature_flags TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.game_catalog_game_aliases ENABLE ROW LEVEL SECURITY;


CREATE POLICY game_catalog_game_aliases_public_read ON public.game_catalog_game_aliases FOR SELECT TO authenticated, anon USING (true);



CREATE POLICY game_catalog_game_aliases_service_role_all ON public.game_catalog_game_aliases TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.game_catalog_game_modes ENABLE ROW LEVEL SECURITY;


CREATE POLICY game_catalog_game_modes_public_read ON public.game_catalog_game_modes FOR SELECT TO authenticated, anon USING (true);



CREATE POLICY game_catalog_game_modes_service_role_all ON public.game_catalog_game_modes TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.game_catalog_games ENABLE ROW LEVEL SECURITY;


CREATE POLICY game_catalog_games_public_read ON public.game_catalog_games FOR SELECT TO authenticated, anon USING (true);



CREATE POLICY game_catalog_games_service_role_all ON public.game_catalog_games TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.game_catalog_tournament_structures ENABLE ROW LEVEL SECURITY;


CREATE POLICY game_catalog_tournament_structures_public_read ON public.game_catalog_tournament_structures FOR SELECT TO authenticated, anon USING (true);



CREATE POLICY game_catalog_tournament_structures_service_role_all ON public.game_catalog_tournament_structures TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.game_catalog_versions ENABLE ROW LEVEL SECURITY;


CREATE POLICY game_catalog_versions_public_read ON public.game_catalog_versions FOR SELECT TO authenticated, anon USING (true);



CREATE POLICY game_catalog_versions_service_role_all ON public.game_catalog_versions TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.game_maps ENABLE ROW LEVEL SECURITY;


CREATE POLICY game_maps_delete_policy ON public.game_maps FOR DELETE USING ((( SELECT auth.role() AS role) = 'service_role'::text));



CREATE POLICY game_maps_insert_policy ON public.game_maps FOR INSERT WITH CHECK ((( SELECT auth.role() AS role) = 'service_role'::text));



CREATE POLICY game_maps_select_policy ON public.game_maps FOR SELECT USING (true);



CREATE POLICY game_maps_update_policy ON public.game_maps FOR UPDATE USING ((( SELECT auth.role() AS role) = 'service_role'::text));



ALTER TABLE public.game_servers ENABLE ROW LEVEL SECURITY;


CREATE POLICY game_servers_delete_service ON public.game_servers FOR DELETE TO service_role USING (true);



CREATE POLICY game_servers_insert_service ON public.game_servers FOR INSERT TO service_role WITH CHECK (true);



CREATE POLICY game_servers_select_service ON public.game_servers FOR SELECT TO service_role USING (true);



CREATE POLICY game_servers_update_service ON public.game_servers FOR UPDATE TO service_role USING (true);



ALTER TABLE public.games_metadata ENABLE ROW LEVEL SECURITY;


CREATE POLICY games_metadata_public_read ON public.games_metadata FOR SELECT USING (true);



ALTER TABLE public.gdpr_requests ENABLE ROW LEVEL SECURITY;


CREATE POLICY gdpr_requests_admin_select ON public.gdpr_requests FOR SELECT TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid()))));



CREATE POLICY gdpr_requests_admin_update ON public.gdpr_requests FOR UPDATE TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid())))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid()))));



CREATE POLICY gdpr_requests_user_insert ON public.gdpr_requests FOR INSERT TO authenticated WITH CHECK ((auth.uid() = user_id));



CREATE POLICY gdpr_requests_user_select ON public.gdpr_requests FOR SELECT TO authenticated USING ((auth.uid() = user_id));



ALTER TABLE public.ghost_approvals ENABLE ROW LEVEL SECURITY;


CREATE POLICY ghost_approvals_service ON public.ghost_approvals TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.ghost_data_access_log ENABLE ROW LEVEL SECURITY;


CREATE POLICY ghost_data_access_log_service ON public.ghost_data_access_log TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.ghost_sessions ENABLE ROW LEVEL SECURITY;


CREATE POLICY ghost_sessions_service ON public.ghost_sessions TO service_role USING (true) WITH CHECK (true);



CREATE POLICY health_snapshots_service_all ON public.station_health_snapshots USING ((auth.role() = 'service_role'::text)) WITH CHECK ((auth.role() = 'service_role'::text));



CREATE POLICY ip_allowlist_super_admin_delete ON public.admin_ip_allowlist FOR DELETE TO authenticated USING ((EXISTS ( SELECT 1
   FROM (public.admin_user_roles aur
     JOIN public.admin_roles ar ON ((ar.id = aur.role_id)))
  WHERE ((aur.user_id = auth.uid()) AND (ar.key = 'super_admin'::text)))));



CREATE POLICY ip_allowlist_super_admin_insert ON public.admin_ip_allowlist FOR INSERT TO authenticated WITH CHECK ((EXISTS ( SELECT 1
   FROM (public.admin_user_roles aur
     JOIN public.admin_roles ar ON ((ar.id = aur.role_id)))
  WHERE ((aur.user_id = auth.uid()) AND (ar.key = 'super_admin'::text)))));



CREATE POLICY ip_allowlist_super_admin_select ON public.admin_ip_allowlist FOR SELECT TO authenticated USING ((EXISTS ( SELECT 1
   FROM (public.admin_user_roles aur
     JOIN public.admin_roles ar ON ((ar.id = aur.role_id)))
  WHERE ((aur.user_id = auth.uid()) AND (ar.key = 'super_admin'::text)))));



CREATE POLICY ip_allowlist_super_admin_update ON public.admin_ip_allowlist FOR UPDATE TO authenticated USING ((EXISTS ( SELECT 1
   FROM (public.admin_user_roles aur
     JOIN public.admin_roles ar ON ((ar.id = aur.role_id)))
  WHERE ((aur.user_id = auth.uid()) AND (ar.key = 'super_admin'::text))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM (public.admin_user_roles aur
     JOIN public.admin_roles ar ON ((ar.id = aur.role_id)))
  WHERE ((aur.user_id = auth.uid()) AND (ar.key = 'super_admin'::text)))));



ALTER TABLE public.leaderboard ENABLE ROW LEVEL SECURITY;


CREATE POLICY leaderboard_select_policy ON public.leaderboard FOR SELECT USING (true);



CREATE POLICY leaderboard_write ON public.leaderboard USING ((( SELECT auth.role() AS role) = 'service_role'::text));



ALTER TABLE public.licenses ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.loyalty_accounts ENABLE ROW LEVEL SECURITY;


CREATE POLICY loyalty_accounts_own_select ON public.loyalty_accounts FOR SELECT USING ((auth.uid() = user_id));



CREATE POLICY loyalty_accounts_service_all ON public.loyalty_accounts USING ((auth.role() = 'service_role'::text));



CREATE POLICY loyalty_accounts_staff_select ON public.loyalty_accounts FOR SELECT USING ((EXISTS ( SELECT 1
   FROM (public.loyalty_transactions lt
     JOIN public.venue_staff vs ON ((vs.venue_id = lt.venue_id)))
  WHERE ((lt.account_id = loyalty_accounts.id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



ALTER TABLE public.loyalty_transactions ENABLE ROW LEVEL SECURITY;


CREATE POLICY loyalty_txn_own_select ON public.loyalty_transactions FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.loyalty_accounts la
  WHERE ((la.id = loyalty_transactions.account_id) AND (la.user_id = auth.uid())))));



CREATE POLICY loyalty_txn_service_all ON public.loyalty_transactions USING ((auth.role() = 'service_role'::text));



CREATE POLICY loyalty_txn_staff_select ON public.loyalty_transactions FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = loyalty_transactions.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



ALTER TABLE public.match_checkins ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.match_completed_events ENABLE ROW LEVEL SECURITY;


CREATE POLICY match_completed_events_enterprise ON public.match_completed_events TO authenticated USING (((auth.role() = 'service_role'::text) OR (CURRENT_USER = 'postgres'::name) OR public.is_admin()));



ALTER TABLE public.match_disputes ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.match_map_veto_actions ENABLE ROW LEVEL SECURITY;


CREATE POLICY match_map_veto_actions_insert_policy ON public.match_map_veto_actions FOR INSERT WITH CHECK (((( SELECT auth.role() AS role) = 'authenticated'::text) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY match_map_veto_actions_select_policy ON public.match_map_veto_actions FOR SELECT USING (true);



ALTER TABLE public.match_map_vetos ENABLE ROW LEVEL SECURITY;


CREATE POLICY match_map_vetos_select_policy ON public.match_map_vetos FOR SELECT USING (true);



ALTER TABLE public.match_messages ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.match_player_stats ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.match_result_reports ENABLE ROW LEVEL SECURITY;


CREATE POLICY match_result_reports_insert ON public.match_result_reports FOR INSERT WITH CHECK ((((auth.uid() = reported_by) AND (EXISTS ( SELECT 1
   FROM (public.brkt_matches m
     JOIN public.team_members tm ON (((tm.team_id = m.team1_id) OR (tm.team_id = m.team2_id))))
  WHERE ((m.id = match_result_reports.match_id) AND (tm.user_id = auth.uid()) AND (tm.is_active = true))))) OR (current_setting('request.jwt.claim.role'::text, true) = 'service_role'::text)));



CREATE POLICY match_result_reports_select ON public.match_result_reports FOR SELECT USING (true);



CREATE POLICY match_result_reports_update ON public.match_result_reports FOR UPDATE USING (((( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM ((public.brkt_matches m
     JOIN public.brkt_versions v ON ((m.version_id = v.id)))
     JOIN public.tournaments t ON ((v.tournament_id = t.id)))
  WHERE ((m.id = match_result_reports.match_id) AND (t.organizer_id = auth.uid())))) OR ((auth.uid() IS NOT NULL) AND (auth.uid() <> reported_by) AND (EXISTS ( SELECT 1
   FROM ((public.brkt_matches m
     JOIN public.brkt_versions v ON ((m.version_id = v.id)))
     JOIN public.tournament_participants tp ON ((tp.tournament_id = v.tournament_id)))
  WHERE ((m.id = match_result_reports.match_id) AND ((tp.user_id = auth.uid()) OR (tp.team_id IN ( SELECT team_members.team_id
           FROM public.team_members
          WHERE ((team_members.user_id = auth.uid()) AND (team_members.is_active = true)))))))))));



CREATE POLICY match_stats_delete_service ON public.match_player_stats FOR DELETE TO service_role USING (true);



CREATE POLICY match_stats_insert_service ON public.match_player_stats FOR INSERT TO service_role WITH CHECK (true);



CREATE POLICY match_stats_select_public ON public.match_player_stats FOR SELECT TO authenticated USING (true);



CREATE POLICY match_stats_select_service ON public.match_player_stats FOR SELECT TO service_role USING (true);



CREATE POLICY match_stats_update_service ON public.match_player_stats FOR UPDATE TO service_role USING (true);



ALTER TABLE public.match_time_proposals ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.member_packages ENABLE ROW LEVEL SECURITY;


CREATE POLICY member_packages_owner_staff_insert ON public.member_packages FOR INSERT WITH CHECK (((EXISTS ( SELECT 1
   FROM public.venues
  WHERE ((venues.id = member_packages.venue_id) AND (venues.owner_id = auth.uid())))) OR (EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = member_packages.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text))))));



CREATE POLICY member_packages_owner_staff_read ON public.member_packages FOR SELECT USING (((EXISTS ( SELECT 1
   FROM public.venues
  WHERE ((venues.id = member_packages.venue_id) AND (venues.owner_id = auth.uid())))) OR (EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = member_packages.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text))))));



CREATE POLICY member_packages_owner_staff_update ON public.member_packages FOR UPDATE USING (((EXISTS ( SELECT 1
   FROM public.venues
  WHERE ((venues.id = member_packages.venue_id) AND (venues.owner_id = auth.uid())))) OR (EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = member_packages.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text))))));



ALTER TABLE public.members ENABLE ROW LEVEL SECURITY;


CREATE POLICY members_service_all ON public.members USING ((current_setting('request.jwt.claim.role'::text, true) = 'service_role'::text));



CREATE POLICY members_staff_select ON public.members FOR SELECT USING (((EXISTS ( SELECT 1
   FROM public.venues
  WHERE ((venues.id = members.venue_id) AND (venues.owner_id = auth.uid())))) OR (EXISTS ( SELECT 1
   FROM public.venue_staff
  WHERE ((venue_staff.venue_id = members.venue_id) AND (venue_staff.user_id = auth.uid()) AND (venue_staff.status = 'active'::text))))));



CREATE POLICY members_user_select ON public.members FOR SELECT USING ((auth.uid() = user_id));



CREATE POLICY messages_participants ON public.match_messages USING ((EXISTS ( SELECT 1
   FROM (((public.brkt_matches m
     JOIN public.brkt_versions v ON ((m.version_id = v.id)))
     JOIN public.tournaments t ON ((v.tournament_id = t.id)))
     LEFT JOIN public.team_members tm ON (((tm.team_id = m.team1_id) OR (tm.team_id = m.team2_id))))
  WHERE ((m.id = match_messages.match_id) AND (((tm.user_id = ( SELECT auth.uid() AS uid)) AND (tm.role = 'captain'::public.team_member_role)) OR (t.organizer_id = ( SELECT auth.uid() AS uid)))))));



ALTER TABLE public.moderation_queue ENABLE ROW LEVEL SECURITY;


CREATE POLICY moderation_queue_admin_delete ON public.moderation_queue FOR DELETE TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid()))));



CREATE POLICY moderation_queue_admin_read ON public.moderation_queue FOR SELECT TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid()))));



CREATE POLICY moderation_queue_admin_update ON public.moderation_queue FOR UPDATE TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid())))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid()))));



CREATE POLICY moderation_queue_authenticated_insert ON public.moderation_queue FOR INSERT TO authenticated WITH CHECK (((status = 'pending'::text) AND (reviewed_by IS NULL) AND (reviewed_at IS NULL) AND (review_notes = ''::text) AND (reported_by = auth.uid())));



CREATE POLICY moderation_queue_service_role_all ON public.moderation_queue TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.notification_preferences ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.notifications ENABLE ROW LEVEL SECURITY;


CREATE POLICY notifications_delete_policy ON public.notifications FOR DELETE USING (((user_id = ( SELECT auth.uid() AS uid)) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY notifications_insert_policy ON public.notifications FOR INSERT WITH CHECK (((EXISTS ( SELECT 1
   FROM public.team_invitations ti
  WHERE ((ti.invited_user_id = notifications.user_id) AND (ti.invited_by_user_id = ( SELECT auth.uid() AS uid))))) OR (EXISTS ( SELECT 1
   FROM public.team_invitations ti
  WHERE ((ti.invited_by_user_id = notifications.user_id) AND (ti.invited_user_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY notifications_select_policy ON public.notifications FOR SELECT USING (((user_id = ( SELECT auth.uid() AS uid)) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY notifications_update_policy ON public.notifications FOR UPDATE USING (((user_id = ( SELECT auth.uid() AS uid)) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY np_delete_own ON public.notification_preferences FOR DELETE USING ((auth.uid() = user_id));



CREATE POLICY np_insert_own ON public.notification_preferences FOR INSERT WITH CHECK ((auth.uid() = user_id));



CREATE POLICY np_select_own ON public.notification_preferences FOR SELECT USING ((auth.uid() = user_id));



CREATE POLICY np_update_own ON public.notification_preferences FOR UPDATE USING ((auth.uid() = user_id));



ALTER TABLE public.operations_audit_log ENABLE ROW LEVEL SECURITY;


CREATE POLICY operations_audit_log_service_role_all ON public.operations_audit_log TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.organization_albums ENABLE ROW LEVEL SECURITY;


CREATE POLICY organization_albums_delete_policy ON public.organization_albums FOR DELETE USING (((organization_id IN ( SELECT organizations.id
   FROM public.organizations
  WHERE (organizations.owner_id = ( SELECT auth.uid() AS uid)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY organization_albums_insert_policy ON public.organization_albums FOR INSERT WITH CHECK (((organization_id IN ( SELECT organizations.id
   FROM public.organizations
  WHERE (organizations.owner_id = ( SELECT auth.uid() AS uid)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY organization_albums_select_policy ON public.organization_albums FOR SELECT USING (true);



CREATE POLICY organization_albums_update_policy ON public.organization_albums FOR UPDATE USING (((organization_id IN ( SELECT organizations.id
   FROM public.organizations
  WHERE (organizations.owner_id = ( SELECT auth.uid() AS uid)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.organization_media ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.organization_staff ENABLE ROW LEVEL SECURITY;


CREATE POLICY organization_staff_self_select ON public.organization_staff FOR SELECT TO authenticated USING ((user_id = auth.uid()));



ALTER TABLE public.organizations ENABLE ROW LEVEL SECURITY;


CREATE POLICY organizations_delete_policy ON public.organizations FOR DELETE USING (((owner_id = ( SELECT auth.uid() AS uid)) OR (( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true))))));



CREATE POLICY organizations_insert_policy ON public.organizations FOR INSERT WITH CHECK (((owner_id = ( SELECT auth.uid() AS uid)) OR (( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true))))));



CREATE POLICY organizations_owner_delete ON public.organizations FOR DELETE USING ((auth.uid() = owner_id));



CREATE POLICY organizations_owner_insert ON public.organizations FOR INSERT WITH CHECK ((auth.uid() = owner_id));



CREATE POLICY organizations_owner_update ON public.organizations FOR UPDATE USING ((auth.uid() = owner_id)) WITH CHECK ((auth.uid() = owner_id));



CREATE POLICY organizations_public_read ON public.organizations FOR SELECT USING (true);



CREATE POLICY organizations_select_policy ON public.organizations FOR SELECT USING (true);



CREATE POLICY organizations_update_policy ON public.organizations FOR UPDATE USING (((owner_id = auth.uid()) OR (auth.role() = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = auth.uid()) AND (profiles.is_admin = true)))) OR (EXISTS ( SELECT 1
   FROM public.organization_staff
  WHERE ((organization_staff.organization_id = organizations.id) AND (organization_staff.user_id = auth.uid()) AND (organization_staff.role = 'admin'::text) AND (organization_staff.status = 'active'::text))))));



ALTER TABLE public.partner_applications ENABLE ROW LEVEL SECURITY;


CREATE POLICY partner_applications_insert_policy ON public.partner_applications FOR INSERT WITH CHECK ((( SELECT auth.role() AS role) = ANY (ARRAY['anon'::text, 'authenticated'::text, 'service_role'::text])));



CREATE POLICY partner_applications_service_role_all ON public.partner_applications TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.partner_sponsor_invitations ENABLE ROW LEVEL SECURITY;


CREATE POLICY partner_sponsor_invitations_service_role_all ON public.partner_sponsor_invitations TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.player_balance ENABLE ROW LEVEL SECURITY;


CREATE POLICY player_balance_own_select ON public.player_balance FOR SELECT USING ((auth.uid() = user_id));



CREATE POLICY player_balance_service_all ON public.player_balance USING ((auth.role() = 'service_role'::text));



ALTER TABLE public.player_steam_accounts ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.pos_orders ENABLE ROW LEVEL SECURITY;


CREATE POLICY pos_orders_owner_all ON public.pos_orders USING ((EXISTS ( SELECT 1
   FROM public.venues v
  WHERE ((v.id = pos_orders.venue_id) AND (v.owner_id = auth.uid()))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.venues v
  WHERE ((v.id = pos_orders.venue_id) AND (v.owner_id = auth.uid())))));



CREATE POLICY pos_orders_service_all ON public.pos_orders USING ((auth.role() = 'service_role'::text));



CREATE POLICY pos_orders_staff_insert ON public.pos_orders FOR INSERT WITH CHECK ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = pos_orders.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



CREATE POLICY pos_orders_staff_select ON public.pos_orders FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = pos_orders.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



CREATE POLICY pos_orders_staff_update ON public.pos_orders FOR UPDATE USING ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = pos_orders.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = pos_orders.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



ALTER TABLE public.profiles ENABLE ROW LEVEL SECURITY;


CREATE POLICY profiles_insert_self ON public.profiles FOR INSERT TO authenticated, service_role WITH CHECK ((id = ( SELECT auth.uid() AS uid)));



CREATE POLICY profiles_read_all ON public.profiles FOR SELECT TO authenticated, anon, service_role USING (true);



CREATE POLICY profiles_update_policy ON public.profiles FOR UPDATE USING (((id = ( SELECT auth.uid() AS uid)) OR public.has_super_admin_role() OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY proposals_match_participants ON public.match_time_proposals USING ((EXISTS ( SELECT 1
   FROM (public.brkt_matches m
     JOIN public.team_members tm ON (((tm.team_id = m.team1_id) OR (tm.team_id = m.team2_id))))
  WHERE ((m.id = match_time_proposals.match_id) AND (tm.user_id = ( SELECT auth.uid() AS uid)) AND (tm.role = 'captain'::public.team_member_role)))));



ALTER TABLE public.report_run_log ENABLE ROW LEVEL SECURITY;


CREATE POLICY report_run_log_admin_insert ON public.report_run_log FOR INSERT TO authenticated WITH CHECK ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid()))));



CREATE POLICY report_run_log_admin_select ON public.report_run_log FOR SELECT TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid()))));



CREATE POLICY report_run_log_admin_update ON public.report_run_log FOR UPDATE TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid())))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid()))));



ALTER TABLE public.report_schedules ENABLE ROW LEVEL SECURITY;


CREATE POLICY report_schedules_admin_delete ON public.report_schedules FOR DELETE TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid()))));



CREATE POLICY report_schedules_admin_insert ON public.report_schedules FOR INSERT TO authenticated WITH CHECK ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid()))));



CREATE POLICY report_schedules_admin_select ON public.report_schedules FOR SELECT TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid()))));



CREATE POLICY report_schedules_admin_update ON public.report_schedules FOR UPDATE TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid())))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.admin_user_roles
  WHERE (admin_user_roles.user_id = auth.uid()))));



ALTER TABLE public.reviews ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.revoked_sessions ENABLE ROW LEVEL SECURITY;


CREATE POLICY revoked_sessions_admin_select ON public.revoked_sessions FOR SELECT TO authenticated USING ((EXISTS ( SELECT 1
   FROM (public.admin_user_roles aur
     JOIN public.admin_roles ar ON ((ar.id = aur.role_id)))
  WHERE ((aur.user_id = auth.uid()) AND (ar.name = ANY (ARRAY['super_admin'::text, 'ops_admin'::text, 'support_admin'::text]))))));



CREATE POLICY revoked_sessions_service_role_all ON public.revoked_sessions TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.riot_accounts ENABLE ROW LEVEL SECURITY;


CREATE POLICY riot_accounts_delete_policy ON public.riot_accounts FOR DELETE USING (((( SELECT auth.uid() AS uid) = user_id) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY riot_accounts_insert_policy ON public.riot_accounts FOR INSERT WITH CHECK (((( SELECT auth.uid() AS uid) = user_id) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY riot_accounts_select_policy ON public.riot_accounts FOR SELECT USING (((( SELECT auth.role() AS role) = 'authenticated'::text) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY riot_accounts_update_policy ON public.riot_accounts FOR UPDATE USING (((( SELECT auth.uid() AS uid) = user_id) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.session_invoices ENABLE ROW LEVEL SECURITY;


CREATE POLICY session_invoices_owner_select ON public.session_invoices FOR SELECT USING (((EXISTS ( SELECT 1
   FROM public.venues
  WHERE ((venues.id = session_invoices.venue_id) AND (venues.owner_id = auth.uid())))) OR (EXISTS ( SELECT 1
   FROM public.venue_staff
  WHERE ((venue_staff.venue_id = session_invoices.venue_id) AND (venue_staff.user_id = auth.uid()) AND (venue_staff.status = 'active'::text))))));



CREATE POLICY session_invoices_service_all ON public.session_invoices USING ((current_setting('request.jwt.claim.role'::text, true) = 'service_role'::text));



ALTER TABLE public.session_refunds ENABLE ROW LEVEL SECURITY;


CREATE POLICY session_refunds_service_all ON public.session_refunds USING ((auth.role() = 'service_role'::text));



CREATE POLICY session_refunds_staff_select ON public.session_refunds FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = session_refunds.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



ALTER TABLE public.sponsor_accounts ENABLE ROW LEVEL SECURITY;


CREATE POLICY sponsor_accounts_delete_policy ON public.sponsor_accounts FOR DELETE USING (((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY sponsor_accounts_insert_policy ON public.sponsor_accounts FOR INSERT WITH CHECK (((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY sponsor_accounts_select_policy ON public.sponsor_accounts FOR SELECT USING (((user_id = ( SELECT auth.uid() AS uid)) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY sponsor_accounts_service_role_all ON public.sponsor_accounts TO service_role USING (true) WITH CHECK (true);



CREATE POLICY sponsor_accounts_update_policy ON public.sponsor_accounts FOR UPDATE USING (((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.sponsor_analytics_events ENABLE ROW LEVEL SECURITY;


CREATE POLICY sponsor_analytics_events_service_role_all ON public.sponsor_analytics_events TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.sponsor_analytics_exports ENABLE ROW LEVEL SECURITY;


CREATE POLICY sponsor_analytics_exports_service_role_all ON public.sponsor_analytics_exports TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.sponsor_audience_daily_facts ENABLE ROW LEVEL SECURITY;


CREATE POLICY sponsor_audience_daily_facts_service_role_all ON public.sponsor_audience_daily_facts TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.sponsor_audience_identities ENABLE ROW LEVEL SECURITY;


CREATE POLICY sponsor_audience_identities_service_role_all ON public.sponsor_audience_identities TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.sponsor_content_daily_stats ENABLE ROW LEVEL SECURITY;


CREATE POLICY sponsor_content_daily_stats_service_role_all ON public.sponsor_content_daily_stats TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.sponsor_daily_totals ENABLE ROW LEVEL SECURITY;


CREATE POLICY sponsor_daily_totals_service_role_all ON public.sponsor_daily_totals TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.sponsor_device_daily_stats ENABLE ROW LEVEL SECURITY;


CREATE POLICY sponsor_device_daily_stats_service_role_all ON public.sponsor_device_daily_stats TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.sponsor_impressions ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.sponsor_placement_daily_stats ENABLE ROW LEVEL SECURITY;


CREATE POLICY sponsor_placement_daily_stats_service_role_all ON public.sponsor_placement_daily_stats TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.sponsor_placements ENABLE ROW LEVEL SECURITY;


CREATE POLICY sponsor_placements_service_role_all ON public.sponsor_placements TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.sponsors ENABLE ROW LEVEL SECURITY;


CREATE POLICY sponsors_delete_policy ON public.sponsors FOR DELETE USING (((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY sponsors_insert_policy ON public.sponsors FOR INSERT WITH CHECK (((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY sponsors_select_policy ON public.sponsors FOR SELECT USING (true);



CREATE POLICY sponsors_update_policy ON public.sponsors FOR UPDATE USING (((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))) OR (id IN ( SELECT sponsor_accounts.sponsor_id
   FROM public.sponsor_accounts
  WHERE ((sponsor_accounts.user_id = ( SELECT auth.uid() AS uid)) AND (sponsor_accounts.role = 'owner'::text)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.staff_audit_log ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.staff_permissions ENABLE ROW LEVEL SECURITY;


CREATE POLICY staff_permissions_service_all ON public.staff_permissions USING ((current_setting('request.jwt.claim.role'::text, true) = 'service_role'::text));



CREATE POLICY staff_permissions_staff_select ON public.staff_permissions FOR SELECT USING (((EXISTS ( SELECT 1
   FROM public.venues
  WHERE ((venues.id = staff_permissions.venue_id) AND (venues.owner_id = auth.uid())))) OR (EXISTS ( SELECT 1
   FROM public.venue_staff
  WHERE ((venue_staff.venue_id = staff_permissions.venue_id) AND (venue_staff.user_id = auth.uid()) AND (venue_staff.status = 'active'::text))))));



ALTER TABLE public.staff_shifts ENABLE ROW LEVEL SECURITY;


CREATE POLICY staff_shifts_service_all ON public.staff_shifts USING ((current_setting('request.jwt.claim.role'::text, true) = 'service_role'::text));



CREATE POLICY staff_shifts_staff_select ON public.staff_shifts FOR SELECT USING (((EXISTS ( SELECT 1
   FROM public.venues
  WHERE ((venues.id = staff_shifts.venue_id) AND (venues.owner_id = auth.uid())))) OR (EXISTS ( SELECT 1
   FROM public.venue_staff
  WHERE ((venue_staff.venue_id = staff_shifts.venue_id) AND (venue_staff.user_id = auth.uid()) AND (venue_staff.status = 'active'::text))))));



ALTER TABLE public.staff_tournament_assignments ENABLE ROW LEVEL SECURITY;


CREATE POLICY staff_tournament_assignments_self_select ON public.staff_tournament_assignments FOR SELECT TO authenticated USING ((EXISTS ( SELECT 1
   FROM public.organization_staff os
  WHERE ((os.id = staff_tournament_assignments.organization_staff_id) AND (os.user_id = auth.uid())))));



ALTER TABLE public.stage_participants ENABLE ROW LEVEL SECURITY;


CREATE POLICY stage_participants_delete_policy ON public.stage_participants FOR DELETE USING (((EXISTS ( SELECT 1
   FROM (public.tournament_stages s
     JOIN public.tournaments t ON ((t.id = s.tournament_id)))
  WHERE ((s.id = stage_participants.stage_id) AND (t.organizer_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY stage_participants_insert_policy ON public.stage_participants FOR INSERT WITH CHECK (((EXISTS ( SELECT 1
   FROM (public.tournament_stages s
     JOIN public.tournaments t ON ((t.id = s.tournament_id)))
  WHERE ((s.id = stage_participants.stage_id) AND (t.organizer_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY stage_participants_select_policy ON public.stage_participants FOR SELECT USING (true);



CREATE POLICY stage_participants_update_policy ON public.stage_participants FOR UPDATE USING (((EXISTS ( SELECT 1
   FROM (public.tournament_stages s
     JOIN public.tournaments t ON ((t.id = s.tournament_id)))
  WHERE ((s.id = stage_participants.stage_id) AND (t.organizer_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.station_health_snapshots ENABLE ROW LEVEL SECURITY;


CREATE POLICY steam_accounts_delete_service ON public.player_steam_accounts FOR DELETE TO service_role USING (true);



CREATE POLICY steam_accounts_insert_service ON public.player_steam_accounts FOR INSERT TO service_role WITH CHECK (true);



CREATE POLICY steam_accounts_select_own ON public.player_steam_accounts FOR SELECT TO authenticated USING ((auth.uid() = user_id));



CREATE POLICY steam_accounts_select_service ON public.player_steam_accounts FOR SELECT TO service_role USING (true);



CREATE POLICY steam_accounts_update_service ON public.player_steam_accounts FOR UPDATE TO service_role USING (true);



ALTER TABLE public.system_config ENABLE ROW LEVEL SECURITY;


CREATE POLICY system_config_service_role_all ON public.system_config TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.system_settings ENABLE ROW LEVEL SECURITY;


CREATE POLICY system_settings_authenticated_read ON public.system_settings FOR SELECT TO authenticated USING ((is_sensitive = false));



CREATE POLICY system_settings_public_read_non_sensitive ON public.system_settings FOR SELECT USING ((is_sensitive = false));



CREATE POLICY system_settings_service_role_all ON public.system_settings TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.team_invitations ENABLE ROW LEVEL SECURITY;


CREATE POLICY team_invitations_delete_policy ON public.team_invitations FOR DELETE USING (((invited_by_user_id = ( SELECT auth.uid() AS uid)) OR (EXISTS ( SELECT 1
   FROM public.teams t
  WHERE ((t.id = team_invitations.team_id) AND (t.owner_id = ( SELECT auth.uid() AS uid))))) OR (EXISTS ( SELECT 1
   FROM public.team_members tm
  WHERE ((tm.team_id = team_invitations.team_id) AND (tm.user_id = ( SELECT auth.uid() AS uid)) AND ((tm.role = 'captain'::public.team_member_role) OR (tm.role = 'owner'::public.team_member_role))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY team_invitations_insert_policy ON public.team_invitations FOR INSERT WITH CHECK (((EXISTS ( SELECT 1
   FROM public.teams t
  WHERE ((t.id = team_invitations.team_id) AND (t.owner_id = ( SELECT auth.uid() AS uid))))) OR (EXISTS ( SELECT 1
   FROM public.team_members tm
  WHERE ((tm.team_id = team_invitations.team_id) AND (tm.user_id = ( SELECT auth.uid() AS uid)) AND ((tm.role = 'captain'::public.team_member_role) OR (tm.role = 'owner'::public.team_member_role))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY team_invitations_select_policy ON public.team_invitations FOR SELECT USING (((invited_user_id = ( SELECT auth.uid() AS uid)) OR (EXISTS ( SELECT 1
   FROM public.teams t
  WHERE ((t.id = team_invitations.team_id) AND (t.owner_id = ( SELECT auth.uid() AS uid))))) OR (EXISTS ( SELECT 1
   FROM public.team_members tm
  WHERE ((tm.team_id = team_invitations.team_id) AND (tm.user_id = ( SELECT auth.uid() AS uid)) AND ((tm.role = 'captain'::public.team_member_role) OR (tm.role = 'owner'::public.team_member_role))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY team_invitations_update_policy ON public.team_invitations FOR UPDATE USING (((invited_user_id = ( SELECT auth.uid() AS uid)) OR (EXISTS ( SELECT 1
   FROM public.teams t
  WHERE ((t.id = team_invitations.team_id) AND (t.owner_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.team_members ENABLE ROW LEVEL SECURITY;


CREATE POLICY team_members_delete_policy ON public.team_members FOR DELETE USING (((user_id = ( SELECT auth.uid() AS uid)) OR (EXISTS ( SELECT 1
   FROM public.teams t
  WHERE ((t.id = team_members.team_id) AND (t.owner_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY team_members_insert_policy ON public.team_members FOR INSERT WITH CHECK (((EXISTS ( SELECT 1
   FROM public.teams t
  WHERE ((t.id = team_members.team_id) AND (t.owner_id = auth.uid())))) OR ((user_id = auth.uid()) AND (EXISTS ( SELECT 1
   FROM public.team_invitations ti
  WHERE ((ti.team_id = team_members.team_id) AND ((ti.invited_user_id = auth.uid()) OR (ti.invited_email = ( SELECT profiles.email
           FROM public.profiles
          WHERE (profiles.id = auth.uid())))) AND (ti.status = 'accepted'::text))))) OR (current_setting('request.jwt.claim.role'::text, true) = 'service_role'::text)));



CREATE POLICY team_members_select_policy ON public.team_members FOR SELECT USING (((is_active = true) OR (user_id = ( SELECT auth.uid() AS uid)) OR public.is_team_owner(team_id) OR (EXISTS ( SELECT 1
   FROM public.teams t
  WHERE ((t.id = team_members.team_id) AND (t.owner_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY team_members_update_policy ON public.team_members FOR UPDATE USING (((user_id = ( SELECT auth.uid() AS uid)) OR (EXISTS ( SELECT 1
   FROM public.teams t
  WHERE ((t.id = team_members.team_id) AND (t.owner_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.team_roster_members ENABLE ROW LEVEL SECURITY;


CREATE POLICY team_roster_members_delete_policy ON public.team_roster_members FOR DELETE USING (((EXISTS ( SELECT 1
   FROM (public.team_rosters r
     JOIN public.teams t ON ((t.id = r.team_id)))
  WHERE ((r.id = team_roster_members.roster_id) AND (t.owner_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY team_roster_members_insert_policy ON public.team_roster_members FOR INSERT WITH CHECK (((EXISTS ( SELECT 1
   FROM (public.team_rosters r
     JOIN public.teams t ON ((t.id = r.team_id)))
  WHERE ((r.id = team_roster_members.roster_id) AND (t.owner_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY team_roster_members_select_policy ON public.team_roster_members FOR SELECT USING (((EXISTS ( SELECT 1
   FROM (public.team_rosters r
     JOIN public.team_members tm ON ((tm.team_id = r.team_id)))
  WHERE ((r.id = team_roster_members.roster_id) AND (tm.user_id = ( SELECT auth.uid() AS uid)) AND (tm.is_active = true)))) OR (EXISTS ( SELECT 1
   FROM (public.team_rosters r
     JOIN public.teams t ON ((t.id = r.team_id)))
  WHERE ((r.id = team_roster_members.roster_id) AND (t.owner_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY team_roster_members_update_policy ON public.team_roster_members FOR UPDATE USING (((EXISTS ( SELECT 1
   FROM (public.team_rosters r
     JOIN public.teams t ON ((t.id = r.team_id)))
  WHERE ((r.id = team_roster_members.roster_id) AND (t.owner_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.team_rosters ENABLE ROW LEVEL SECURITY;


CREATE POLICY team_rosters_delete_policy ON public.team_rosters FOR DELETE USING (((EXISTS ( SELECT 1
   FROM public.teams t
  WHERE ((t.id = team_rosters.team_id) AND (t.owner_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY team_rosters_insert_policy ON public.team_rosters FOR INSERT WITH CHECK (((EXISTS ( SELECT 1
   FROM public.teams t
  WHERE ((t.id = team_rosters.team_id) AND (t.owner_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY team_rosters_select_policy ON public.team_rosters FOR SELECT USING (((EXISTS ( SELECT 1
   FROM public.team_members tm
  WHERE ((tm.team_id = team_rosters.team_id) AND (tm.user_id = ( SELECT auth.uid() AS uid)) AND (tm.is_active = true)))) OR (EXISTS ( SELECT 1
   FROM public.teams t
  WHERE ((t.id = team_rosters.team_id) AND (t.owner_id = ( SELECT auth.uid() AS uid))))) OR (EXISTS ( SELECT 1
   FROM public.team_invitations ti
  WHERE ((ti.roster_id = team_rosters.id) AND (ti.status = 'pending'::text) AND ((ti.invited_user_id = ( SELECT auth.uid() AS uid)) OR (ti.invited_email = ( SELECT profiles.email
           FROM public.profiles
          WHERE (profiles.id = ( SELECT auth.uid() AS uid)))))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY team_rosters_update_policy ON public.team_rosters FOR UPDATE USING (((EXISTS ( SELECT 1
   FROM public.teams t
  WHERE ((t.id = team_rosters.team_id) AND (t.owner_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.teams ENABLE ROW LEVEL SECURITY;


CREATE POLICY teams_delete_policy ON public.teams FOR DELETE USING (((owner_id = ( SELECT auth.uid() AS uid)) OR (( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true))))));



CREATE POLICY teams_insert_policy ON public.teams FOR INSERT WITH CHECK (((owner_id = ( SELECT auth.uid() AS uid)) OR (( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true))))));



CREATE POLICY teams_select_policy ON public.teams FOR SELECT USING (((deleted_at IS NULL) OR (owner_id = ( SELECT auth.uid() AS uid)) OR (( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true))))));



CREATE POLICY teams_update_policy ON public.teams FOR UPDATE USING (((owner_id = ( SELECT auth.uid() AS uid)) OR (( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true))))));



ALTER TABLE public.tournament_announcements ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.tournament_bans ENABLE ROW LEVEL SECURITY;


CREATE POLICY tournament_bans_delete_policy ON public.tournament_bans FOR DELETE USING (((EXISTS ( SELECT 1
   FROM public.tournaments t
  WHERE ((t.id = tournament_bans.tournament_id) AND (t.organizer_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY tournament_bans_insert_policy ON public.tournament_bans FOR INSERT WITH CHECK (((EXISTS ( SELECT 1
   FROM public.tournaments t
  WHERE ((t.id = tournament_bans.tournament_id) AND (t.organizer_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY tournament_bans_select_policy ON public.tournament_bans FOR SELECT USING (true);



CREATE POLICY tournament_bans_update_policy ON public.tournament_bans FOR UPDATE USING (((EXISTS ( SELECT 1
   FROM public.tournaments t
  WHERE ((t.id = tournament_bans.tournament_id) AND (t.organizer_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.tournament_disputes ENABLE ROW LEVEL SECURITY;


CREATE POLICY tournament_disputes_delete_policy ON public.tournament_disputes FOR DELETE USING (((EXISTS ( SELECT 1
   FROM public.tournaments t
  WHERE ((t.id = tournament_disputes.tournament_id) AND (t.organizer_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY tournament_disputes_insert_policy ON public.tournament_disputes FOR INSERT WITH CHECK (((raised_by_user_id = ( SELECT auth.uid() AS uid)) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.tournament_invitations ENABLE ROW LEVEL SECURITY;


CREATE POLICY tournament_invitations_authenticated_read_own ON public.tournament_invitations FOR SELECT TO authenticated USING ((lower(email) = lower(COALESCE((auth.jwt() ->> 'email'::text), ''::text))));



CREATE POLICY tournament_invitations_service_role_all ON public.tournament_invitations TO service_role USING (true) WITH CHECK (true);



ALTER TABLE public.tournament_map_pools ENABLE ROW LEVEL SECURITY;


CREATE POLICY tournament_map_pools_delete_policy ON public.tournament_map_pools FOR DELETE USING (((EXISTS ( SELECT 1
   FROM public.tournaments t
  WHERE ((t.id = tournament_map_pools.tournament_id) AND (t.organizer_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY tournament_map_pools_insert_policy ON public.tournament_map_pools FOR INSERT WITH CHECK (((EXISTS ( SELECT 1
   FROM public.tournaments t
  WHERE ((t.id = tournament_map_pools.tournament_id) AND (t.organizer_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY tournament_map_pools_select_policy ON public.tournament_map_pools FOR SELECT USING (true);



CREATE POLICY tournament_map_pools_update_policy ON public.tournament_map_pools FOR UPDATE USING (((EXISTS ( SELECT 1
   FROM public.tournaments t
  WHERE ((t.id = tournament_map_pools.tournament_id) AND (t.organizer_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.tournament_match_results ENABLE ROW LEVEL SECURITY;


CREATE POLICY tournament_match_results_insert_policy ON public.tournament_match_results FOR INSERT WITH CHECK (((reporter_user_id = ( SELECT auth.uid() AS uid)) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY tournament_match_results_select_policy ON public.tournament_match_results FOR SELECT USING (((reporter_user_id = ( SELECT auth.uid() AS uid)) OR (EXISTS ( SELECT 1
   FROM public.tournaments t
  WHERE ((t.id = tournament_match_results.tournament_id) AND (t.organizer_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.tournament_participants ENABLE ROW LEVEL SECURITY;


CREATE POLICY tournament_participants_delete_policy ON public.tournament_participants FOR DELETE USING (((user_id = ( SELECT auth.uid() AS uid)) OR (team_captain_id = ( SELECT auth.uid() AS uid)) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY tournament_participants_insert_policy ON public.tournament_participants FOR INSERT WITH CHECK (((((user_id = auth.uid()) OR (team_captain_id = auth.uid())) AND (EXISTS ( SELECT 1
   FROM public.tournaments t
  WHERE ((t.id = tournament_participants.tournament_id) AND ((t.entry_fee IS NULL) OR (t.entry_fee = (0)::numeric)) AND (t.status = 'open'::public.tournament_status))))) OR (current_setting('request.jwt.claim.role'::text, true) = 'service_role'::text)));



CREATE POLICY tournament_participants_select_policy ON public.tournament_participants FOR SELECT USING (true);



CREATE POLICY tournament_participants_update_policy ON public.tournament_participants FOR UPDATE USING (((user_id = ( SELECT auth.uid() AS uid)) OR (team_captain_id = ( SELECT auth.uid() AS uid)) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.tournament_stages ENABLE ROW LEVEL SECURITY;


CREATE POLICY tournament_stages_delete_policy ON public.tournament_stages FOR DELETE USING (((EXISTS ( SELECT 1
   FROM public.tournaments t
  WHERE ((t.id = tournament_stages.tournament_id) AND (t.organizer_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY tournament_stages_insert_policy ON public.tournament_stages FOR INSERT WITH CHECK (((EXISTS ( SELECT 1
   FROM public.tournaments t
  WHERE ((t.id = tournament_stages.tournament_id) AND (t.organizer_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY tournament_stages_select_policy ON public.tournament_stages FOR SELECT USING (true);



CREATE POLICY tournament_stages_update_policy ON public.tournament_stages FOR UPDATE USING (((EXISTS ( SELECT 1
   FROM public.tournaments t
  WHERE ((t.id = tournament_stages.tournament_id) AND (t.organizer_id = ( SELECT auth.uid() AS uid))))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.tournaments ENABLE ROW LEVEL SECURITY;


CREATE POLICY tournaments_enterprise_delete ON public.tournaments FOR DELETE TO authenticated USING ((public.is_admin() OR (organizer_id = auth.uid())));



CREATE POLICY tournaments_enterprise_select ON public.tournaments FOR SELECT TO authenticated USING ((((is_public = true) AND (status <> 'draft'::public.tournament_status)) OR public.is_admin() OR (organizer_id = auth.uid()) OR (organization_id IN ( SELECT organizations.id
   FROM public.organizations
  WHERE (organizations.owner_id = auth.uid()))) OR (id IN ( SELECT tournament_participants.tournament_id
   FROM public.tournament_participants
  WHERE (tournament_participants.user_id = auth.uid())))));



CREATE POLICY tournaments_enterprise_update ON public.tournaments FOR UPDATE TO authenticated USING ((public.is_admin() OR (organizer_id = auth.uid()) OR (organization_id IN ( SELECT organizations.id
   FROM public.organizations
  WHERE (organizations.owner_id = auth.uid())))));



CREATE POLICY tournaments_insert_policy ON public.tournaments FOR INSERT WITH CHECK ((((( SELECT auth.role() AS role) = 'authenticated'::text) AND (organizer_id = ( SELECT auth.uid() AS uid))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.user_roles ENABLE ROW LEVEL SECURITY;


CREATE POLICY user_roles_delete_policy ON public.user_roles FOR DELETE USING (((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY user_roles_insert_policy ON public.user_roles FOR INSERT WITH CHECK (((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY user_roles_select_policy ON public.user_roles FOR SELECT USING (true);



CREATE POLICY user_roles_update_policy ON public.user_roles FOR UPDATE USING (((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.venue_announcements ENABLE ROW LEVEL SECURITY;


CREATE POLICY venue_announcements_owner_all ON public.venue_announcements USING ((EXISTS ( SELECT 1
   FROM public.venues v
  WHERE ((v.id = venue_announcements.venue_id) AND (v.owner_id = auth.uid()))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.venues v
  WHERE ((v.id = venue_announcements.venue_id) AND (v.owner_id = auth.uid())))));



CREATE POLICY venue_announcements_public_select ON public.venue_announcements FOR SELECT USING ((is_active = true));



CREATE POLICY venue_announcements_service_all ON public.venue_announcements USING ((auth.role() = 'service_role'::text));



CREATE POLICY venue_announcements_staff_insert ON public.venue_announcements FOR INSERT WITH CHECK ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = venue_announcements.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



CREATE POLICY venue_announcements_staff_select ON public.venue_announcements FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = venue_announcements.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



CREATE POLICY venue_announcements_staff_update ON public.venue_announcements FOR UPDATE USING ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = venue_announcements.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = venue_announcements.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



CREATE POLICY venue_avail_public_read ON public.venue_availability_snapshot FOR SELECT USING (true);



CREATE POLICY venue_avail_service_all ON public.venue_availability_snapshot USING ((auth.role() = 'service_role'::text));



ALTER TABLE public.venue_availability ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.venue_availability_snapshot ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.venue_billing_config ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.venue_bookings ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.venue_combos ENABLE ROW LEVEL SECURITY;


CREATE POLICY venue_combos_owner_all ON public.venue_combos USING ((EXISTS ( SELECT 1
   FROM public.venues v
  WHERE ((v.id = venue_combos.venue_id) AND (v.owner_id = auth.uid()))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.venues v
  WHERE ((v.id = venue_combos.venue_id) AND (v.owner_id = auth.uid())))));



CREATE POLICY venue_combos_public_select ON public.venue_combos FOR SELECT USING ((is_active = true));



CREATE POLICY venue_combos_service_all ON public.venue_combos USING ((auth.role() = 'service_role'::text));



CREATE POLICY venue_combos_staff_insert ON public.venue_combos FOR INSERT WITH CHECK ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = venue_combos.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



CREATE POLICY venue_combos_staff_select ON public.venue_combos FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = venue_combos.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



CREATE POLICY venue_combos_staff_update ON public.venue_combos FOR UPDATE USING ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = venue_combos.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = venue_combos.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



ALTER TABLE public.venue_impressions ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.venue_live_status ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.venue_loyalty_config ENABLE ROW LEVEL SECURITY;


CREATE POLICY venue_loyalty_config_owner_select ON public.venue_loyalty_config FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venues v
  WHERE ((v.id = venue_loyalty_config.venue_id) AND (v.owner_id = auth.uid())))));



CREATE POLICY venue_loyalty_config_owner_update ON public.venue_loyalty_config FOR UPDATE USING ((EXISTS ( SELECT 1
   FROM public.venues v
  WHERE ((v.id = venue_loyalty_config.venue_id) AND (v.owner_id = auth.uid()))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.venues v
  WHERE ((v.id = venue_loyalty_config.venue_id) AND (v.owner_id = auth.uid())))));



CREATE POLICY venue_loyalty_config_public_select ON public.venue_loyalty_config FOR SELECT USING ((is_active = true));



CREATE POLICY venue_loyalty_config_service_all ON public.venue_loyalty_config USING ((auth.role() = 'service_role'::text));



CREATE POLICY venue_loyalty_config_staff_select ON public.venue_loyalty_config FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = venue_loyalty_config.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



ALTER TABLE public.venue_menu_items ENABLE ROW LEVEL SECURITY;


CREATE POLICY venue_menu_items_owner_all ON public.venue_menu_items USING ((EXISTS ( SELECT 1
   FROM public.venues v
  WHERE ((v.id = venue_menu_items.venue_id) AND (v.owner_id = auth.uid()))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.venues v
  WHERE ((v.id = venue_menu_items.venue_id) AND (v.owner_id = auth.uid())))));



CREATE POLICY venue_menu_items_public_select ON public.venue_menu_items FOR SELECT USING ((is_available = true));



CREATE POLICY venue_menu_items_service_all ON public.venue_menu_items USING ((auth.role() = 'service_role'::text));



CREATE POLICY venue_menu_items_staff_insert ON public.venue_menu_items FOR INSERT WITH CHECK ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = venue_menu_items.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



CREATE POLICY venue_menu_items_staff_select ON public.venue_menu_items FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = venue_menu_items.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



CREATE POLICY venue_menu_items_staff_update ON public.venue_menu_items FOR UPDATE USING ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = venue_menu_items.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = venue_menu_items.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



ALTER TABLE public.venue_packages ENABLE ROW LEVEL SECURITY;


CREATE POLICY venue_packages_owner_staff_insert ON public.venue_packages FOR INSERT WITH CHECK (((EXISTS ( SELECT 1
   FROM public.venues
  WHERE ((venues.id = venue_packages.venue_id) AND (venues.owner_id = auth.uid())))) OR (EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = venue_packages.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text))))));



CREATE POLICY venue_packages_owner_staff_update ON public.venue_packages FOR UPDATE USING (((EXISTS ( SELECT 1
   FROM public.venues
  WHERE ((venues.id = venue_packages.venue_id) AND (venues.owner_id = auth.uid())))) OR (EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = venue_packages.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text))))));



CREATE POLICY venue_packages_public_read ON public.venue_packages FOR SELECT USING (true);



ALTER TABLE public.venue_receipts ENABLE ROW LEVEL SECURITY;


CREATE POLICY venue_receipts_owner_select ON public.venue_receipts FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venues
  WHERE ((venues.id = venue_receipts.venue_id) AND (venues.owner_id = auth.uid())))));



CREATE POLICY venue_receipts_service_all ON public.venue_receipts USING ((auth.role() = 'service_role'::text));



ALTER TABLE public.venue_reviews ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.venue_session_events ENABLE ROW LEVEL SECURITY;


CREATE POLICY venue_session_events_service_all ON public.venue_session_events USING ((auth.role() = 'service_role'::text)) WITH CHECK ((auth.role() = 'service_role'::text));



ALTER TABLE public.venue_sessions ENABLE ROW LEVEL SECURITY;


CREATE POLICY venue_sessions_owner_select ON public.venue_sessions FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venues
  WHERE ((venues.id = venue_sessions.venue_id) AND (venues.owner_id = auth.uid())))));



CREATE POLICY venue_sessions_service_all ON public.venue_sessions USING ((current_setting('request.jwt.claim.role'::text, true) = 'service_role'::text));



CREATE POLICY venue_sessions_user_select ON public.venue_sessions FOR SELECT USING ((auth.uid() = user_id));



ALTER TABLE public.venue_staff ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.venue_staff_invites ENABLE ROW LEVEL SECURITY;


CREATE POLICY venue_staff_invites_service_all ON public.venue_staff_invites USING ((auth.role() = 'service_role'::text));



CREATE POLICY venue_staff_own_select ON public.venue_staff FOR SELECT USING ((auth.uid() = user_id));



CREATE POLICY venue_staff_owner_all ON public.venue_staff USING ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = venue_staff.venue_id) AND (vs.user_id = auth.uid()) AND (vs.role = 'owner'::text)))));



CREATE POLICY venue_staff_service_all ON public.venue_staff USING ((auth.role() = 'service_role'::text));



ALTER TABLE public.venue_station_status ENABLE ROW LEVEL SECURITY;


ALTER TABLE public.venue_stations ENABLE ROW LEVEL SECURITY;


CREATE POLICY venue_stations_owner_select ON public.venue_stations FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venues v
  WHERE ((v.id = venue_stations.venue_id) AND (v.owner_id = auth.uid())))));



CREATE POLICY venue_stations_service_role_all ON public.venue_stations USING ((auth.role() = 'service_role'::text)) WITH CHECK ((auth.role() = 'service_role'::text));



ALTER TABLE public.venues ENABLE ROW LEVEL SECURITY;


CREATE POLICY venues_delete_policy ON public.venues FOR DELETE USING (((owner_id = ( SELECT auth.uid() AS uid)) OR (( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true))))));



CREATE POLICY venues_insert_policy ON public.venues FOR INSERT WITH CHECK (((owner_id = ( SELECT auth.uid() AS uid)) OR (( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true))))));



CREATE POLICY venues_select_policy ON public.venues FOR SELECT USING (((deleted_at IS NULL) OR (owner_id = ( SELECT auth.uid() AS uid)) OR (( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true))))));



CREATE POLICY venues_update_policy ON public.venues FOR UPDATE USING (((owner_id = ( SELECT auth.uid() AS uid)) OR (( SELECT auth.role() AS role) = 'service_role'::text) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true))))));



ALTER TABLE public.verification_requests ENABLE ROW LEVEL SECURITY;


CREATE POLICY verification_requests_delete_policy ON public.verification_requests FOR DELETE USING (((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY verification_requests_insert_policy ON public.verification_requests FOR INSERT WITH CHECK (((user_id = ( SELECT auth.uid() AS uid)) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY verification_requests_select_policy ON public.verification_requests FOR SELECT USING (((user_id = ( SELECT auth.uid() AS uid)) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY verification_requests_update_policy ON public.verification_requests FOR UPDATE USING ((((user_id = ( SELECT auth.uid() AS uid)) AND (status = 'pending'::text)) OR (EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.verified_roles ENABLE ROW LEVEL SECURITY;


CREATE POLICY verified_roles_delete_policy ON public.verified_roles FOR DELETE USING (((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY verified_roles_insert_policy ON public.verified_roles FOR INSERT WITH CHECK (((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



CREATE POLICY verified_roles_select_policy ON public.verified_roles FOR SELECT USING (true);



CREATE POLICY verified_roles_update_policy ON public.verified_roles FOR UPDATE USING (((EXISTS ( SELECT 1
   FROM public.profiles
  WHERE ((profiles.id = ( SELECT auth.uid() AS uid)) AND (profiles.is_admin = true)))) OR (( SELECT auth.role() AS role) = 'service_role'::text)));



ALTER TABLE public.walk_in_queue ENABLE ROW LEVEL SECURITY;


CREATE POLICY walk_in_queue_owner_select ON public.walk_in_queue FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venues
  WHERE ((venues.id = walk_in_queue.venue_id) AND (venues.owner_id = auth.uid())))));



CREATE POLICY walk_in_queue_service_all ON public.walk_in_queue USING ((current_setting('request.jwt.claim.role'::text, true) = 'service_role'::text));



CREATE POLICY walk_in_queue_staff_select ON public.walk_in_queue FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venue_staff
  WHERE ((venue_staff.venue_id = walk_in_queue.venue_id) AND (venue_staff.user_id = auth.uid()) AND (venue_staff.status = 'active'::text)))));



ALTER TABLE public.wallet_transactions ENABLE ROW LEVEL SECURITY;


CREATE POLICY wallet_txn_own_select ON public.wallet_transactions FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.customer_wallets cw
  WHERE ((cw.id = wallet_transactions.wallet_id) AND (cw.user_id = auth.uid())))));



CREATE POLICY wallet_txn_service_all ON public.wallet_transactions USING ((auth.role() = 'service_role'::text));



CREATE POLICY wallet_txn_staff_insert ON public.wallet_transactions FOR INSERT WITH CHECK ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = wallet_transactions.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



CREATE POLICY wallet_txn_staff_select ON public.wallet_transactions FOR SELECT USING ((EXISTS ( SELECT 1
   FROM public.venue_staff vs
  WHERE ((vs.venue_id = wallet_transactions.venue_id) AND (vs.user_id = auth.uid()) AND (vs.status = 'active'::text)))));



ALTER TABLE public.zones ENABLE ROW LEVEL SECURITY;


CREATE POLICY zones_owner_select ON public.zones FOR SELECT USING (((EXISTS ( SELECT 1
   FROM public.venues
  WHERE ((venues.id = zones.venue_id) AND (venues.owner_id = auth.uid())))) OR (EXISTS ( SELECT 1
   FROM public.venue_staff
  WHERE ((venue_staff.venue_id = zones.venue_id) AND (venue_staff.user_id = auth.uid()) AND (venue_staff.status = 'active'::text))))));



CREATE POLICY zones_service_all ON public.zones USING ((current_setting('request.jwt.claim.role'::text, true) = 'service_role'::text));



