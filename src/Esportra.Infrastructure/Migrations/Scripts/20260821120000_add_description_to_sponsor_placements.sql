-- Add per-placement copy/description for partner_showcase sections
ALTER TABLE public.sponsor_placements
    ADD COLUMN IF NOT EXISTS description TEXT;
