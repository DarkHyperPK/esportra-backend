-- Gap 8: Add updated_at column to practice_sessions
ALTER TABLE public.practice_sessions
    ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT now();
