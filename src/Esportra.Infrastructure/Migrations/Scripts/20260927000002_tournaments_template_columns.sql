-- Add template reference and venue address columns to tournaments table
-- template_id links to tournament_templates for quick-create defaults
-- venue_address supports LAN event address storage

ALTER TABLE tournaments ADD COLUMN IF NOT EXISTS template_id uuid;
ALTER TABLE tournaments ADD COLUMN IF NOT EXISTS venue_address text;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_tournaments_template_id') THEN
    ALTER TABLE tournaments ADD CONSTRAINT fk_tournaments_template_id
      FOREIGN KEY (template_id) REFERENCES tournament_templates(id);
  END IF;
END $$;
