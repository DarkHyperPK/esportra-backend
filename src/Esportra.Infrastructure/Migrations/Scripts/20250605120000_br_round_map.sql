-- Persist per-round map selection for Battle Royale rounds.
ALTER TABLE public.br_rounds ADD COLUMN IF NOT EXISTS map TEXT NULL;
