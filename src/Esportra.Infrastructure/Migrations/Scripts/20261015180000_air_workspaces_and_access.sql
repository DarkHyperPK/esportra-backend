-- Air workspaces are independent of tournament organizations and organizer verification.
CREATE TABLE public.air_plans (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), name text NOT NULL CHECK(length(name) BETWEEN 1 AND 80),
 production_limit integer CHECK(production_limit >= 0), capture_limit integer CHECK(capture_limit >= 0), enabled boolean NOT NULL DEFAULT true
);
INSERT INTO public.air_plans(id,name) VALUES ('06100000-0000-4000-8000-000000000001','14-day trial');
CREATE TABLE public.air_accounts (
 user_id uuid PRIMARY KEY REFERENCES public.profiles(id), license_id uuid NOT NULL REFERENCES public.licenses(id),
 trial_started_at timestamptz NOT NULL DEFAULT now(), trial_ends_at timestamptz NOT NULL DEFAULT now()+interval '14 days',
 plan_id uuid NOT NULL REFERENCES public.air_plans(id), valid_until timestamptz, status text NOT NULL DEFAULT 'active' CHECK(status IN ('active','suspended','revoked')), revision integer NOT NULL DEFAULT 1
);
CREATE TABLE public.air_workspaces (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), owner_id uuid NOT NULL REFERENCES public.air_accounts(user_id),
 name text NOT NULL CHECK(length(name) BETWEEN 1 AND 120), created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX air_workspaces_owner ON public.air_workspaces(owner_id);
CREATE TABLE public.air_projects (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), workspace_id uuid NOT NULL REFERENCES public.air_workspaces(id),
 title text NOT NULL CHECK(length(title) BETWEEN 1 AND 120), revision integer NOT NULL DEFAULT 1 CHECK(revision>0),
 document jsonb NOT NULL CHECK(jsonb_typeof(document)='object' AND octet_length(document::text)<=2097152),
 created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), archived_at timestamptz
);
CREATE INDEX air_projects_workspace ON public.air_projects(workspace_id,updated_at DESC);
CREATE TABLE public.air_devices (
 id uuid PRIMARY KEY, user_id uuid NOT NULL REFERENCES public.air_accounts(user_id), name text NOT NULL CHECK(length(name) BETWEEN 1 AND 80),
 mode text NOT NULL CHECK(mode IN ('production','capture-only')), status text NOT NULL DEFAULT 'active' CHECK(status IN ('active','revoked')),
 last_seen_at timestamptz NOT NULL DEFAULT now(), revision integer NOT NULL DEFAULT 1,
 health jsonb NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(health)='object' AND octet_length(health::text)<=4096)
);
CREATE INDEX air_devices_user ON public.air_devices(user_id,status);
CREATE TABLE public.air_assets (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), workspace_id uuid NOT NULL REFERENCES public.air_workspaces(id),
 digest text NOT NULL, mime_type text NOT NULL CHECK(mime_type='image/png'), payload bytea NOT NULL CHECK(octet_length(payload)<=5242880),
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE public.air_admin_audit (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, actor_id uuid NOT NULL REFERENCES public.profiles(id),
 action text NOT NULL, subject_id text NOT NULL, reason text NOT NULL, created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE public.air_releases (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), version text NOT NULL, channel text NOT NULL CHECK(channel IN ('stable','preview')),
 download_url text NOT NULL, notes text NOT NULL, published boolean NOT NULL DEFAULT false, created_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE public.air_plans ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.air_accounts ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.air_workspaces ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.air_projects ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.air_devices ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.air_assets ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.air_admin_audit ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.air_releases ENABLE ROW LEVEL SECURITY;
-- Endpoints use the existing server connection and explicit ownership checks. No direct client writes.
REVOKE ALL ON public.air_plans,public.air_accounts,public.air_workspaces,public.air_projects,public.air_devices,public.air_assets,public.air_admin_audit,public.air_releases FROM anon,authenticated;
