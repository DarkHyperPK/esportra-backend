-- Missing hit details can be retried without repeatedly fetching the same
-- incomplete match on every stats refresh. Existing archive RLS is unchanged.
ALTER TABLE public.valorant_match_backlog ADD COLUMN IF NOT EXISTS retry_after timestamptz;
