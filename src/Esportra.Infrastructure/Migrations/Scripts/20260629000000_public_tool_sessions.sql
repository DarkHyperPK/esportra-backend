-- Standalone public bracket and map veto tools.
-- These tables are intentionally separate from tournament/stage/match tables so
-- public tools do not weaken tournament authorization or FK assumptions.

CREATE TABLE IF NOT EXISTS public.public_tool_brackets (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    owner_user_id uuid NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    title text NOT NULL,
    format text NOT NULL CHECK (format IN ('single_elimination', 'double_elimination')),
    best_of integer NOT NULL DEFAULT 1 CHECK (best_of IN (1, 3, 5)),
    status text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'active', 'completed')),
    visibility text NOT NULL DEFAULT 'private' CHECK (visibility IN ('private', 'unlisted')),
    share_token text UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS public.public_tool_bracket_versions (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    bracket_id uuid NOT NULL REFERENCES public.public_tool_brackets(id) ON DELETE CASCADE,
    version_number integer NOT NULL DEFAULT 1,
    status text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'archived')),
    graph_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (bracket_id, version_number)
);

CREATE INDEX IF NOT EXISTS idx_public_tool_brackets_owner
    ON public.public_tool_brackets(owner_user_id, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_public_tool_brackets_share_token
    ON public.public_tool_brackets(share_token)
    WHERE share_token IS NOT NULL;

CREATE TABLE IF NOT EXISTS public.public_veto_sessions (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    game text NOT NULL,
    best_of integer NOT NULL CHECK (best_of IN (1, 3, 5)),
    team1_name text NOT NULL,
    team2_name text NOT NULL,
    team1_id uuid NOT NULL DEFAULT gen_random_uuid(),
    team2_id uuid NOT NULL DEFAULT gen_random_uuid(),
    status text NOT NULL DEFAULT 'in_progress' CHECK (status IN ('in_progress', 'completed', 'cancelled')),
    current_team_id uuid,
    current_action text CHECK (current_action IN ('ban', 'pick', 'pick_side')),
    current_action_number integer NOT NULL DEFAULT 1,
    team1_banned_maps text[] NOT NULL DEFAULT '{}',
    team2_banned_maps text[] NOT NULL DEFAULT '{}',
    team1_picked_maps jsonb NOT NULL DEFAULT '[]'::jsonb,
    team2_picked_maps jsonb NOT NULL DEFAULT '[]'::jsonb,
    selected_map_id text,
    selected_map_pool text[] NOT NULL DEFAULT '{}',
    host_token text NOT NULL UNIQUE,
    team1_token text NOT NULL UNIQUE,
    team2_token text NOT NULL UNIQUE,
    started_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    expires_at timestamptz NOT NULL DEFAULT (now() + interval '24 hours'),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS public.public_veto_actions (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id uuid NOT NULL REFERENCES public.public_veto_sessions(id) ON DELETE CASCADE,
    team_id uuid NOT NULL,
    team_side text NOT NULL CHECK (team_side IN ('team1', 'team2')),
    action_type text NOT NULL CHECK (action_type IN ('ban', 'pick', 'pick_side')),
    map_id text NOT NULL,
    action_number integer NOT NULL,
    side text,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_public_veto_sessions_tokens
    ON public.public_veto_sessions(host_token, team1_token, team2_token);

CREATE INDEX IF NOT EXISTS idx_public_veto_sessions_expires_at
    ON public.public_veto_sessions(expires_at);

CREATE INDEX IF NOT EXISTS idx_public_veto_actions_session
    ON public.public_veto_actions(session_id, action_number);
