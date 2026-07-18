-- Unified sponsor placement assignments (tournament-specific + global)
CREATE TABLE IF NOT EXISTS public.sponsor_placements (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    sponsor_id UUID NOT NULL REFERENCES public.sponsors(id) ON DELETE CASCADE,
    tournament_id UUID REFERENCES public.tournaments(id) ON DELETE CASCADE,
    placement_zone TEXT NOT NULL CHECK (placement_zone IN (
        'homepage_ticker', 'partner_showcase', 'sidebar_partner',
        'wide_partner', 'card_badge', 'partner_logo'
    )),
    banner_url TEXT,
    logo_url TEXT,
    headline TEXT,
    cta_text TEXT,
    cta_url TEXT,
    priority INT NOT NULL DEFAULT 0,
    is_active BOOLEAN NOT NULL DEFAULT true,
    assigned_by UUID REFERENCES public.profiles(id),
    starts_at TIMESTAMPTZ,
    ends_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- One sponsor per zone per tournament (NULL-safe for global placements)
CREATE UNIQUE INDEX IF NOT EXISTS sponsor_placements_unique_idx
    ON public.sponsor_placements (sponsor_id, COALESCE(tournament_id, '00000000-0000-0000-0000-000000000000'::uuid), placement_zone);

-- Query: all active placements for a tournament
CREATE INDEX IF NOT EXISTS sponsor_placements_tournament_active_idx
    ON public.sponsor_placements (tournament_id, placement_zone, is_active)
    WHERE is_active = true;

-- Query: all placements for a sponsor
CREATE INDEX IF NOT EXISTS sponsor_placements_sponsor_idx
    ON public.sponsor_placements (sponsor_id, is_active);

-- Query: global placements (tournament_id IS NULL)
CREATE INDEX IF NOT EXISTS sponsor_placements_global_idx
    ON public.sponsor_placements (placement_zone)
    WHERE tournament_id IS NULL AND is_active = true;

-- RLS
ALTER TABLE public.sponsor_placements ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.sponsor_placements FORCE ROW LEVEL SECURITY;
REVOKE ALL ON public.sponsor_placements FROM anon, authenticated;
DROP POLICY IF EXISTS sponsor_placements_service_role_all ON public.sponsor_placements;
CREATE POLICY sponsor_placements_service_role_all ON public.sponsor_placements
    FOR ALL TO service_role USING (true) WITH CHECK (true);
GRANT ALL ON public.sponsor_placements TO service_role;
