-- Add faceit_nickname to profiles (mirrors riot_tag pattern)
ALTER TABLE public.profiles ADD COLUMN IF NOT EXISTS faceit_nickname text;

-- Create faceit_accounts table (mirrors riot_accounts structure)
CREATE TABLE IF NOT EXISTS public.faceit_accounts (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    faceit_id text NOT NULL,
    nickname text NOT NULL,
    avatar_url text,
    access_token text,
    refresh_token text,
    token_expires_at timestamp with time zone,
    linked_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    CONSTRAINT faceit_accounts_pkey PRIMARY KEY (id),
    CONSTRAINT faceit_accounts_user_id_key UNIQUE (user_id),
    CONSTRAINT faceit_accounts_faceit_id_key UNIQUE (faceit_id),
    CONSTRAINT faceit_accounts_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_faceit_accounts_user_id ON public.faceit_accounts(user_id);
CREATE INDEX IF NOT EXISTS idx_faceit_accounts_faceit_id ON public.faceit_accounts(faceit_id);

-- RLS
ALTER TABLE public.faceit_accounts ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS faceit_accounts_select_policy ON public.faceit_accounts;
CREATE POLICY faceit_accounts_select_policy ON public.faceit_accounts
    FOR SELECT USING (
        user_id = ( SELECT auth.uid() AS uid)
        OR ( SELECT auth.role() AS role) = 'service_role'::text
    );

DROP POLICY IF EXISTS faceit_accounts_delete_policy ON public.faceit_accounts;
CREATE POLICY faceit_accounts_delete_policy ON public.faceit_accounts
    FOR DELETE USING (
        user_id = ( SELECT auth.uid() AS uid)
        OR ( SELECT auth.role() AS role) = 'service_role'::text
    );

-- Update get_roster_members RPC to expose faceit_nickname alongside riot_tag
DROP FUNCTION IF EXISTS public.get_roster_members(uuid);
CREATE OR REPLACE FUNCTION public.get_roster_members(r_id uuid)
RETURNS TABLE(user_id uuid, username text, full_name text, riot_tag text, steam_tag text, faceit_nickname text)
LANGUAGE plpgsql
AS $$
declare
  t_id uuid;
begin
  select team_id into t_id from team_rosters where id = r_id;

  return query
  with roster_members as (
    select m.user_id, p.username, p.full_name, p.riot_tag, p.steam_tag, p.faceit_nickname
    from team_roster_members m
    join profiles p on p.id = m.user_id
    where m.roster_id = r_id
  ),
  team_owner as (
    select t.owner_id as user_id, p.username, p.full_name, p.riot_tag, p.steam_tag, p.faceit_nickname
    from teams t
    join profiles p on p.id = t.owner_id
    where t.id = t_id
  )
  select user_id, username, full_name, riot_tag, steam_tag, faceit_nickname from roster_members
  union
  select user_id, username, full_name, riot_tag, steam_tag, faceit_nickname from team_owner;
end;
$$;
