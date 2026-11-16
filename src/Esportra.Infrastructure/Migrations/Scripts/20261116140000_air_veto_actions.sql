-- Veto steps Esportra Air enters in the match room for a match it produces (LAN events where staff collect
-- the veto and the producer types it in). One row per step Air sent: a retry with the same client key gets
-- the same answer instead of entering the step twice.
CREATE TABLE IF NOT EXISTS public.air_veto_actions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  match_id uuid NOT NULL,
  client_key text NOT NULL CHECK (length(client_key) BETWEEN 8 AND 80),
  user_id uuid NOT NULL REFERENCES public.profiles(id),
  action text NOT NULL CHECK (action IN ('start','toss_choice','ban','pick','pick_side','reset')),
  result text NOT NULL CHECK (length(result) <= 200000),
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS air_veto_actions_key ON public.air_veto_actions(match_id, client_key);
CREATE INDEX IF NOT EXISTS air_veto_actions_by_match ON public.air_veto_actions(match_id, created_at DESC);
ALTER TABLE public.air_veto_actions ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON public.air_veto_actions FROM anon, authenticated;
