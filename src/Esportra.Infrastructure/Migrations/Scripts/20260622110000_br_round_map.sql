-- Persist per-round map selection for Battle Royale rounds.
-- NOTE: br_rounds was dropped by 20260611120000_br_pro_lobby_model.sql
-- This migration is kept for journal consistency but is now a no-op.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'br_rounds' AND table_schema = 'public') THEN
        ALTER TABLE public.br_rounds ADD COLUMN IF NOT EXISTS map TEXT NULL;
    END IF;
END $$;
