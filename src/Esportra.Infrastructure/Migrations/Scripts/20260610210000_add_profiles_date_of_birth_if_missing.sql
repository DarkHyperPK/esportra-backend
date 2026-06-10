-- Ensure profile DOB column exists for signup/backfill flows on older staging schemas.
ALTER TABLE public.profiles ADD COLUMN IF NOT EXISTS date_of_birth DATE;
