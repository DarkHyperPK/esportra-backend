-- Enable Supabase Realtime for tables subscribed to in CaptainMatchPage
-- and useMatchResultReport hook.
--
-- Without REPLICA IDENTITY FULL + publication membership, all postgres_changes
-- subscriptions on these tables are silent (no events delivered).

ALTER TABLE public.brkt_matches REPLICA IDENTITY FULL;
ALTER TABLE public.brkt_match_games REPLICA IDENTITY FULL;
ALTER TABLE public.match_result_reports REPLICA IDENTITY FULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_publication_tables
        WHERE pubname = 'supabase_realtime'
          AND schemaname = 'public'
          AND tablename = 'brkt_matches'
    ) THEN
        ALTER PUBLICATION supabase_realtime ADD TABLE public.brkt_matches;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_publication_tables
        WHERE pubname = 'supabase_realtime'
          AND schemaname = 'public'
          AND tablename = 'brkt_match_games'
    ) THEN
        ALTER PUBLICATION supabase_realtime ADD TABLE public.brkt_match_games;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_publication_tables
        WHERE pubname = 'supabase_realtime'
          AND schemaname = 'public'
          AND tablename = 'match_result_reports'
    ) THEN
        ALTER PUBLICATION supabase_realtime ADD TABLE public.match_result_reports;
    END IF;
END $$;
