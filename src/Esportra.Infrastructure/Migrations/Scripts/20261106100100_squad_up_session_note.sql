-- A short line a player shows on their Squad Up card ("I smoke, you entry"). Optional, 100 characters at most.
ALTER TABLE public.lfg_swipe_sessions ADD COLUMN IF NOT EXISTS note TEXT;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_lfg_swipe_sessions_note') THEN
    ALTER TABLE public.lfg_swipe_sessions ADD CONSTRAINT chk_lfg_swipe_sessions_note CHECK (note IS NULL OR char_length(note) <= 100);
  END IF;
END $$;
