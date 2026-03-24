-- Create storage buckets needed for dispute evidence and match evidence.
-- Uses ON CONFLICT to be idempotent (safe to run if buckets already exist).

INSERT INTO storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
VALUES
    ('match-evidence',
     'match-evidence',
     true,
     5242880,  -- 5 MB
     ARRAY['image/jpeg','image/png','image/webp','image/gif','image/jpg'])
ON CONFLICT (id) DO NOTHING;

INSERT INTO storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
VALUES
    ('tournaments.disputes.evidence',
     'tournaments.disputes.evidence',
     true,
     5242880,
     ARRAY['image/jpeg','image/png','image/webp','image/gif','image/jpg'])
ON CONFLICT (id) DO NOTHING;

-- ── match-evidence policies ────────────────────────────────────────────────

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'match_evidence_public_select'
    ) THEN
        CREATE POLICY match_evidence_public_select
            ON storage.objects FOR SELECT
            TO public
            USING (bucket_id = 'match-evidence');
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'match_evidence_auth_insert'
    ) THEN
        CREATE POLICY match_evidence_auth_insert
            ON storage.objects FOR INSERT
            TO authenticated
            WITH CHECK (bucket_id = 'match-evidence');
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'match_evidence_auth_delete'
    ) THEN
        CREATE POLICY match_evidence_auth_delete
            ON storage.objects FOR DELETE
            TO authenticated
            USING (bucket_id = 'match-evidence' AND owner = auth.uid());
    END IF;
END;
$$;

-- ── tournaments.disputes.evidence policies ─────────────────────────────────

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'dispute_evidence_public_select'
    ) THEN
        CREATE POLICY dispute_evidence_public_select
            ON storage.objects FOR SELECT
            TO public
            USING (bucket_id = 'tournaments.disputes.evidence');
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'dispute_evidence_auth_insert'
    ) THEN
        CREATE POLICY dispute_evidence_auth_insert
            ON storage.objects FOR INSERT
            TO authenticated
            WITH CHECK (bucket_id = 'tournaments.disputes.evidence');
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'dispute_evidence_auth_delete'
    ) THEN
        CREATE POLICY dispute_evidence_auth_delete
            ON storage.objects FOR DELETE
            TO authenticated
            USING (bucket_id = 'tournaments.disputes.evidence' AND owner = auth.uid());
    END IF;
END;
$$;
