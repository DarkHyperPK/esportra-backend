-- Account-owned presentation settings. Never changes official match records.
CREATE TABLE IF NOT EXISTS air_production_drafts (
    owner_id uuid NOT NULL REFERENCES auth.users(id),
    match_id uuid NOT NULL REFERENCES brkt_matches(id),
    revision bigint NOT NULL DEFAULT 1,
    document jsonb NOT NULL DEFAULT '{}'::jsonb,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (owner_id, match_id)
);
ALTER TABLE air_production_drafts ENABLE ROW LEVEL SECURITY;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename='air_production_drafts' AND policyname='air_production_drafts_owner') THEN
        CREATE POLICY air_production_drafts_owner ON air_production_drafts FOR ALL USING (owner_id=auth.uid()) WITH CHECK (owner_id=auth.uid());
    END IF;
END $$;
CREATE TABLE IF NOT EXISTS air_package_events (
    id uuid PRIMARY KEY,
    package_id uuid NOT NULL REFERENCES air_match_packages(id),
    version integer NOT NULL,
    occurred_at timestamptz NOT NULL,
    event_type text NOT NULL,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb
);
CREATE INDEX IF NOT EXISTS ix_air_package_events_package ON air_package_events(package_id, occurred_at);
ALTER TABLE air_package_events ENABLE ROW LEVEL SECURITY;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename='air_package_events' AND policyname='air_package_events_owner') THEN
        CREATE POLICY air_package_events_owner ON air_package_events FOR SELECT USING (EXISTS(SELECT 1 FROM air_match_packages p WHERE p.id=package_id AND p.owner_id=auth.uid()));
    END IF;
END $$;

REVOKE ALL ON air_production_drafts,air_package_events FROM anon,authenticated;
