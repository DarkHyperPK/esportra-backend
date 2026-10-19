-- Account-owned packages do not create or modify tournament organizations/shows.
CREATE TABLE IF NOT EXISTS public.air_match_packages (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 owner_id uuid NOT NULL REFERENCES public.air_accounts(user_id),
 match_id uuid NOT NULL REFERENCES public.brkt_matches(id),
 latest_version integer NOT NULL DEFAULT 0 CHECK(latest_version >= 0),
 created_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(owner_id,match_id)
);
CREATE TABLE IF NOT EXISTS public.air_match_package_versions (
 package_id uuid NOT NULL REFERENCES public.air_match_packages(id),
 version integer NOT NULL CHECK(version > 0),
 request_id uuid NOT NULL,
 source_hash text NOT NULL CHECK(length(source_hash)=64),
 content_hash text NOT NULL CHECK(length(content_hash)=64),
 configuration text NOT NULL CHECK(jsonb_typeof(configuration::jsonb)='object' AND octet_length(configuration)<=2097152),
 created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(package_id,version), UNIQUE(package_id,request_id)
);
ALTER TABLE public.air_match_packages ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.air_match_package_versions ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON public.air_match_packages,public.air_match_package_versions FROM anon,authenticated;
CREATE OR REPLACE FUNCTION public.air_package_version_immutable() RETURNS trigger
LANGUAGE plpgsql AS $$ BEGIN
 RAISE EXCEPTION 'Published Air package versions are immutable';
END $$;
DO $$ BEGIN
 IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname='air_package_version_immutable') THEN
  CREATE TRIGGER air_package_version_immutable BEFORE UPDATE OR DELETE ON public.air_match_package_versions
  FOR EACH ROW EXECUTE FUNCTION public.air_package_version_immutable();
 END IF;
END $$;
