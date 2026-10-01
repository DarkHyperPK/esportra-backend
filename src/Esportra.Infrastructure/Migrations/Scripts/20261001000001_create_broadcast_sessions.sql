CREATE TABLE IF NOT EXISTS broadcast_sessions (
    id             uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    organizer_id   uuid        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    match_id       uuid        REFERENCES brkt_matches(id) ON DELETE SET NULL,
    match_code     text        NOT NULL,           -- short code shown in control panel (e.g. 'GF1')
    series_map     smallint    NOT NULL DEFAULT 1, -- current map number in series (1-indexed)
    series_total   smallint    NOT NULL DEFAULT 1, -- total maps in series (BO1=1, BO3=3, BO5=5)
    app_version    text,                           -- desktop app version that created this session
    status         text        NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'completed', 'abandoned')),
    last_seen_at   timestamptz NOT NULL DEFAULT now(),
    created_at     timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE broadcast_sessions ENABLE ROW LEVEL SECURITY;

-- Organizer can manage their own sessions
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_sessions' AND policyname = 'organizer_own_sessions'
  ) THEN
    CREATE POLICY "organizer_own_sessions" ON broadcast_sessions
        FOR ALL TO authenticated
        USING (organizer_id = auth.uid())
        WITH CHECK (organizer_id = auth.uid());
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS broadcast_sessions_organizer_idx ON broadcast_sessions (organizer_id);
CREATE INDEX IF NOT EXISTS broadcast_sessions_match_id_idx  ON broadcast_sessions (match_id);
