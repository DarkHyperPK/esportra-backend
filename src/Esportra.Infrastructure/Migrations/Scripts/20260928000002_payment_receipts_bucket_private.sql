-- Make tournaments.payment.receipts bucket private.
-- Original design intent (per upload endpoint comment) was always private.
-- Two migrations in June 2026 incorrectly flipped it public. This reverts that.
-- The backend receipt proxy endpoint authenticates with the service_role key and
-- is unaffected by this change. All receipt access must go through that endpoint.

-- Drop the public select policy added by 20260615140000 and 20260615150000
DROP POLICY IF EXISTS payment_receipts_public_select ON storage.objects;

-- Flip the bucket back to private
UPDATE storage.buckets
SET public = false
WHERE id = 'tournaments.payment.receipts';
