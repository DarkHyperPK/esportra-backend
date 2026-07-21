-- Add deterministic, enforceable slot identity to sponsor placements.
ALTER TABLE public.sponsor_placements
    ADD COLUMN IF NOT EXISTS slot_number INTEGER;

-- Scope rules changed card_badge to a per-tournament placement. Preserve invalid
-- legacy records for review, but ensure they cannot render or reserve inventory.
UPDATE public.sponsor_placements
SET is_active = false,
    slot_number = NULL,
    updated_at = NOW()
WHERE (placement_zone IN ('homepage_ticker', 'partner_showcase') AND tournament_id IS NOT NULL)
   OR (placement_zone IN ('sidebar_partner', 'wide_partner', 'card_badge', 'partner_logo') AND tournament_id IS NULL);

WITH ranked AS (
    SELECT id,
           ROW_NUMBER() OVER (
               PARTITION BY COALESCE(tournament_id, '00000000-0000-0000-0000-000000000000'::uuid), placement_zone
               ORDER BY priority DESC, created_at ASC, id ASC
           ) AS assigned_slot,
           CASE placement_zone
               WHEN 'homepage_ticker' THEN 10
               WHEN 'partner_showcase' THEN 6
               WHEN 'sidebar_partner' THEN 2
               WHEN 'wide_partner' THEN 4
               WHEN 'card_badge' THEN 1
               WHEN 'partner_logo' THEN 4
           END AS capacity
    FROM public.sponsor_placements
    WHERE (placement_zone IN ('homepage_ticker', 'partner_showcase') AND tournament_id IS NULL)
       OR (placement_zone IN ('sidebar_partner', 'wide_partner', 'card_badge', 'partner_logo') AND tournament_id IS NOT NULL)
)
UPDATE public.sponsor_placements placement
SET slot_number = CASE WHEN ranked.assigned_slot <= ranked.capacity THEN ranked.assigned_slot END,
    is_active = CASE WHEN ranked.assigned_slot <= ranked.capacity THEN placement.is_active ELSE false END,
    updated_at = CASE WHEN ranked.assigned_slot <= ranked.capacity THEN placement.updated_at ELSE NOW() END
FROM ranked
WHERE placement.id = ranked.id;

DROP INDEX IF EXISTS public.sponsor_placements_unique_idx;

CREATE UNIQUE INDEX IF NOT EXISTS sponsor_placements_sponsor_scope_zone_unique_idx
    ON public.sponsor_placements (
        sponsor_id,
        COALESCE(tournament_id, '00000000-0000-0000-0000-000000000000'::uuid),
        placement_zone
    );

CREATE UNIQUE INDEX IF NOT EXISTS sponsor_placements_scope_zone_slot_unique_idx
    ON public.sponsor_placements (
        COALESCE(tournament_id, '00000000-0000-0000-0000-000000000000'::uuid),
        placement_zone,
        slot_number
    )
    WHERE slot_number IS NOT NULL;

CREATE INDEX IF NOT EXISTS sponsor_placements_inventory_idx
    ON public.sponsor_placements (tournament_id, placement_zone, slot_number, is_active);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'sponsor_placements_scope_check'
    ) THEN
        ALTER TABLE public.sponsor_placements
            ADD CONSTRAINT sponsor_placements_scope_check CHECK (
                (placement_zone IN ('homepage_ticker', 'partner_showcase') AND tournament_id IS NULL)
                OR
                (placement_zone IN ('sidebar_partner', 'wide_partner', 'card_badge', 'partner_logo') AND tournament_id IS NOT NULL)
            ) NOT VALID;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'sponsor_placements_slot_check'
    ) THEN
        ALTER TABLE public.sponsor_placements
            ADD CONSTRAINT sponsor_placements_slot_check CHECK (
                slot_number IS NULL
                OR slot_number BETWEEN 1 AND CASE placement_zone
                    WHEN 'homepage_ticker' THEN 10
                    WHEN 'partner_showcase' THEN 6
                    WHEN 'sidebar_partner' THEN 2
                    WHEN 'wide_partner' THEN 4
                    WHEN 'card_badge' THEN 1
                    WHEN 'partner_logo' THEN 4
                END
            );
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'sponsor_placements_schedule_check'
    ) THEN
        ALTER TABLE public.sponsor_placements
            ADD CONSTRAINT sponsor_placements_schedule_check CHECK (
                starts_at IS NULL OR ends_at IS NULL OR starts_at < ends_at
            );
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'sponsor_placements_priority_check'
    ) THEN
        ALTER TABLE public.sponsor_placements
            ADD CONSTRAINT sponsor_placements_priority_check CHECK (priority BETWEEN -1000 AND 1000);
    END IF;
END $$;
