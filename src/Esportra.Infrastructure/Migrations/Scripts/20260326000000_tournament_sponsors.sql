-- Tournament-Sponsor linking table for ad placement management
CREATE TABLE IF NOT EXISTS tournament_sponsors (
    id              uuid DEFAULT gen_random_uuid() PRIMARY KEY,
    tournament_id   uuid NOT NULL REFERENCES tournaments(id) ON DELETE CASCADE,
    sponsor_id      uuid NOT NULL REFERENCES sponsors(id) ON DELETE CASCADE,
    sponsor_type    text NOT NULL DEFAULT 'event_sponsor'
                    CHECK (sponsor_type IN ('title_sponsor', 'event_sponsor', 'media_sponsor')),
    placement_zones text[] NOT NULL DEFAULT '{}',
    media_overrides jsonb NOT NULL DEFAULT '{}',
    priority        int NOT NULL DEFAULT 0,
    is_active       boolean NOT NULL DEFAULT true,
    assigned_by     uuid REFERENCES profiles(id),
    created_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tournament_id, sponsor_id)
);

ALTER TABLE tournament_sponsors ENABLE ROW LEVEL SECURITY;

-- Public can read active tournament sponsors
CREATE POLICY "tournament_sponsors_public_read" ON tournament_sponsors
    FOR SELECT USING (is_active = true);

-- Admins can manage via SECURITY DEFINER RPCs or direct postgres access
CREATE POLICY "tournament_sponsors_admin_all" ON tournament_sponsors
    FOR ALL USING (
        EXISTS (SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid())
    );

-- Tournament organizers can manage their own tournament sponsors
CREATE POLICY "tournament_sponsors_organizer_manage" ON tournament_sponsors
    FOR ALL USING (
        EXISTS (
            SELECT 1 FROM tournaments t
            WHERE t.id = tournament_sponsors.tournament_id
            AND t.organizer_id = auth.uid()
        )
    );

-- Add tournament_id to sponsor_impressions for per-tournament tracking
ALTER TABLE sponsor_impressions ADD COLUMN IF NOT EXISTS tournament_id uuid REFERENCES tournaments(id) ON DELETE SET NULL;

-- Index for fast lookups
CREATE INDEX IF NOT EXISTS idx_tournament_sponsors_tournament ON tournament_sponsors(tournament_id);
CREATE INDEX IF NOT EXISTS idx_tournament_sponsors_sponsor ON tournament_sponsors(sponsor_id);
CREATE INDEX IF NOT EXISTS idx_sponsor_impressions_tournament ON sponsor_impressions(tournament_id) WHERE tournament_id IS NOT NULL;
