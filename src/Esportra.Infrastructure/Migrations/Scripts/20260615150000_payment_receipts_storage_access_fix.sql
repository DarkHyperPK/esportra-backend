-- Repair public read access for payment receipt storage objects.
-- Public /object/public/ URLs are served to anon (no JWT); policy must allow anon SELECT.

UPDATE storage.buckets
SET public = true
WHERE id = 'tournaments.payment.receipts';

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'payment_receipts_public_select'
    ) THEN
        DROP POLICY payment_receipts_public_select ON storage.objects;
    END IF;

    CREATE POLICY payment_receipts_public_select
        ON storage.objects FOR SELECT
        USING (bucket_id = 'tournaments.payment.receipts');
END;
$$;
