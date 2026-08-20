-- Preserve legacy placement conflicts as explicit, resolvable review records.
ALTER TABLE public.sponsor_placements
    ADD COLUMN IF NOT EXISTS review_reason TEXT;

-- Temporarily drop scope_check so we can tag invalid-scope rows with a reason.
-- The updated constraint below will exclude rows under review.
ALTER TABLE public.sponsor_placements DROP CONSTRAINT IF EXISTS sponsor_placements_scope_check;

UPDATE public.sponsor_placements
SET review_reason = CASE
        WHEN (placement_zone IN ('homepage_ticker', 'partner_showcase') AND tournament_id IS NOT NULL)
          OR (placement_zone IN ('sidebar_partner', 'wide_partner', 'card_badge', 'partner_logo') AND tournament_id IS NULL)
            THEN 'invalid_scope'
        WHEN slot_number IS NULL THEN 'overflow'
        ELSE review_reason
    END,
    is_active = CASE WHEN slot_number IS NULL THEN false ELSE is_active END,
    updated_at = CASE WHEN slot_number IS NULL THEN NOW() ELSE updated_at END
WHERE review_reason IS NULL;

-- Re-add scope check, now excluding rows under review (they have known invalid scope).
ALTER TABLE public.sponsor_placements
    ADD CONSTRAINT sponsor_placements_scope_check CHECK (
        review_reason IS NOT NULL
        OR (placement_zone IN ('homepage_ticker', 'partner_showcase') AND tournament_id IS NULL)
        OR (placement_zone IN ('sidebar_partner', 'wide_partner', 'card_badge', 'partner_logo') AND tournament_id IS NOT NULL)
    ) NOT VALID;

DROP INDEX IF EXISTS public.sponsor_placements_sponsor_scope_zone_unique_idx;
CREATE UNIQUE INDEX IF NOT EXISTS sponsor_placements_sponsor_scope_zone_unique_idx
    ON public.sponsor_placements (
        sponsor_id,
        COALESCE(tournament_id, '00000000-0000-0000-0000-000000000000'::uuid),
        placement_zone
    )
    WHERE review_reason IS NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'sponsor_placements_review_reason_check'
    ) THEN
        ALTER TABLE public.sponsor_placements
            ADD CONSTRAINT sponsor_placements_review_reason_check CHECK (
                review_reason IS NULL OR review_reason IN ('overflow', 'invalid_scope', 'unassigned')
            );
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS sponsor_placements_review_idx
    ON public.sponsor_placements (review_reason, updated_at DESC)
    WHERE review_reason IS NOT NULL;
