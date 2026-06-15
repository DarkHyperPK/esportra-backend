-- Allow public read access for payment receipt objects (bucket is public).
-- Receipt URLs are still only exposed via organizer-authenticated API endpoints.

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'payment_receipts_public_select'
    ) THEN
        CREATE POLICY payment_receipts_public_select
            ON storage.objects FOR SELECT
            TO public
            USING (bucket_id = 'tournaments.payment.receipts');
    END IF;
END;
$$;
