-- Add human-readable reference number to tournament_disputes.
-- Format: DSP-0001, DSP-0002, etc.

CREATE SEQUENCE IF NOT EXISTS dispute_reference_seq START WITH 1;

ALTER TABLE public.tournament_disputes
    ADD COLUMN IF NOT EXISTS reference_number TEXT;

-- Backfill existing disputes in creation order
WITH numbered AS (
    SELECT id, ROW_NUMBER() OVER (ORDER BY created_at) AS rn
    FROM public.tournament_disputes
    WHERE reference_number IS NULL
)
UPDATE public.tournament_disputes d
SET reference_number = 'DSP-' || LPAD(n.rn::text, 4, '0')
FROM numbered n
WHERE d.id = n.id;

-- Advance the sequence past existing disputes
SELECT setval('dispute_reference_seq',
    GREATEST((SELECT COUNT(*) FROM public.tournament_disputes), 1));

-- Ensure uniqueness
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'tournament_disputes_reference_number_key'
    ) THEN
        ALTER TABLE public.tournament_disputes
            ADD CONSTRAINT tournament_disputes_reference_number_key UNIQUE (reference_number);
    END IF;
END $$;
