-- Air access requests: a person whose Air access has ended asks for it from inside Air; a super admin
-- grants or declines it from the admin Air page. One open request per person.
CREATE TABLE IF NOT EXISTS public.air_access_requests (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL REFERENCES public.profiles(id),
  note text NOT NULL DEFAULT '' CHECK (length(note) <= 500),
  status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'granted', 'declined', 'withdrawn')),
  reason text CHECK (reason IS NULL OR length(reason) <= 500),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  decided_by uuid REFERENCES public.profiles(id),
  decided_at timestamptz
);
CREATE UNIQUE INDEX IF NOT EXISTS air_access_requests_one_open ON public.air_access_requests(user_id) WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS air_access_requests_by_status ON public.air_access_requests(status, created_at DESC);
CREATE INDEX IF NOT EXISTS air_access_requests_by_user ON public.air_access_requests(user_id, created_at DESC);
ALTER TABLE public.air_access_requests ENABLE ROW LEVEL SECURITY;
-- Endpoints use the server connection with explicit checks; no direct client access (default deny).
REVOKE ALL ON public.air_access_requests FROM anon, authenticated;
