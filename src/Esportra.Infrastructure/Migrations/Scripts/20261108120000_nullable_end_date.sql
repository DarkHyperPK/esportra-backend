-- Allow tournaments to have no end date (open-ended scheduling)
ALTER TABLE tournaments ALTER COLUMN end_date DROP NOT NULL;
