-- Payment flow: columns, storage bucket, and RLS policies
-- Adds payment columns to tournament_participants and tournaments,
-- creates the private tournaments.payment.receipts bucket,
-- and adds it to the service-role storage policies.

-- ── 1. Payment columns on tournament_participants ──────────────────────────
ALTER TABLE tournament_participants
    ADD COLUMN IF NOT EXISTS payment_status           text DEFAULT 'not_required',
    ADD COLUMN IF NOT EXISTS payment_receipt_url       text,
    ADD COLUMN IF NOT EXISTS payment_rejection_reason  text;

-- ── 2. Payment instructions on tournaments ─────────────────────────────────
ALTER TABLE tournaments
    ADD COLUMN IF NOT EXISTS payment_instructions text;

-- ── 3. Storage bucket (private — receipts are sensitive) ───────────────────
INSERT INTO storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
VALUES (
    'tournaments.payment.receipts',
    'tournaments.payment.receipts',
    false,
    5242880,  -- 5 MB
    ARRAY['image/jpeg','image/png','image/webp','application/pdf']
)
ON CONFLICT (id) DO NOTHING;

-- ── 4. Update service-role storage policies to include the new bucket ──────

-- service UPDATE
DO $$
BEGIN
    -- Drop and recreate to add the new bucket to the array
    IF EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage' AND tablename = 'objects'
          AND policyname = 'storage_service_update'
    ) THEN
        DROP POLICY storage_service_update ON storage.objects;
    END IF;

    CREATE POLICY storage_service_update ON storage.objects FOR UPDATE USING (
        bucket_id = ANY (ARRAY[
            'teams.logos','tournaments.banners','tournaments.media','tournaments.results',
            'users.avatars','users.uploads','users.documents.kyc',
            'system.assets.website','system.assets.games','system.assets.partners','system.assets.sponsors',
            'venue-images','venues.images','venues.layouts',
            'organizer-banners','organizer-media',
            'match-evidence','tournaments.disputes.evidence',
            'system.notifications.attachments','system.temp',
            'tournaments.payment.receipts'
        ])
    );
END;
$$;

-- service DELETE
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage' AND tablename = 'objects'
          AND policyname = 'storage_service_delete'
    ) THEN
        DROP POLICY storage_service_delete ON storage.objects;
    END IF;

    CREATE POLICY storage_service_delete ON storage.objects FOR DELETE USING (
        bucket_id = ANY (ARRAY[
            'teams.logos','tournaments.banners','tournaments.media','tournaments.results',
            'users.avatars','users.uploads','users.documents.kyc',
            'system.assets.website','system.assets.games','system.assets.partners','system.assets.sponsors',
            'venue-images','venues.images','venues.layouts',
            'organizer-banners','organizer-media',
            'match-evidence','tournaments.disputes.evidence',
            'system.notifications.attachments','system.temp',
            'tournaments.payment.receipts'
        ])
    );
END;
$$;

-- private SELECT (service-role can read private buckets)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage' AND tablename = 'objects'
          AND policyname = 'storage_private_select'
    ) THEN
        DROP POLICY storage_private_select ON storage.objects;
    END IF;

    CREATE POLICY storage_private_select ON storage.objects FOR SELECT USING (
        bucket_id = ANY (ARRAY[
            'users.documents.kyc','system.temp',
            'tournaments.payment.receipts'
        ])
    );
END;
$$;
