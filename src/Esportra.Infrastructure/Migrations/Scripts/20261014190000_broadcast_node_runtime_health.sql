-- Runtime diagnostics remain separate from static device capabilities.
-- Existing broadcast_nodes RLS and organization ownership policies are unchanged.
ALTER TABLE public.broadcast_nodes
    ADD COLUMN IF NOT EXISTS runtime_status jsonb,
    ADD COLUMN IF NOT EXISTS runtime_seen_at timestamptz;
