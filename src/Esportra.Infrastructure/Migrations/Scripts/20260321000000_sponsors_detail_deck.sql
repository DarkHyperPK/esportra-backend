-- Add detail_deck_url column for partner campaign deck uploads (PDF/PPTX)
ALTER TABLE sponsors ADD COLUMN IF NOT EXISTS detail_deck_url text;
