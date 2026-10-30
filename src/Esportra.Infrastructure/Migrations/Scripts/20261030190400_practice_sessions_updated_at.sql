-- PROJ-077 M4: Add updated_at to practice_sessions
-- The original migration omitted this column. PracticeSessionRow (required
-- positional parameter) and several UPDATE statements all reference it.
-- ADD COLUMN ... DEFAULT fills existing rows with now() at migration time.

ALTER TABLE public.practice_sessions
    ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT now();
