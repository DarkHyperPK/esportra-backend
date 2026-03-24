-- Enable Supabase Realtime for map veto tables.
-- Both tables need:
--   1. REPLICA IDENTITY FULL so that row-level filters (match_id=eq.X)
--      work correctly on UPDATE and DELETE events.
--   2. Added to the supabase_realtime publication so events actually fire.

ALTER TABLE public.match_map_vetos REPLICA IDENTITY FULL;
ALTER TABLE public.match_map_veto_actions REPLICA IDENTITY FULL;

-- Add to publication (idempotent — IF EXISTS guard not needed, ADD is safe to re-run)
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_publication_tables
        WHERE pubname = 'supabase_realtime'
          AND schemaname = 'public'
          AND tablename = 'match_map_vetos'
    ) THEN
        ALTER PUBLICATION supabase_realtime ADD TABLE public.match_map_vetos;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_publication_tables
        WHERE pubname = 'supabase_realtime'
          AND schemaname = 'public'
          AND tablename = 'match_map_veto_actions'
    ) THEN
        ALTER PUBLICATION supabase_realtime ADD TABLE public.match_map_veto_actions;
    END IF;
END $$;
