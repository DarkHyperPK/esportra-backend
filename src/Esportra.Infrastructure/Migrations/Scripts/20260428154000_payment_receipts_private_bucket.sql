-- Payment receipts contain sensitive financial evidence and must stay private.
-- Serve them only through short-lived signed URLs from the backend.

UPDATE storage.buckets
SET public = FALSE
WHERE id = 'tournaments.payment.receipts';

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage' AND tablename = 'objects'
          AND policyname = 'storage_private_select'
    ) THEN
        DROP POLICY storage_private_select ON storage.objects;
    END IF;

    CREATE POLICY storage_private_select ON storage.objects FOR SELECT TO service_role USING (
        bucket_id = ANY (ARRAY[
            'users.documents.kyc',
            'system.temp',
            'tournaments.payment.receipts'
        ])
    );
END;
$$;
