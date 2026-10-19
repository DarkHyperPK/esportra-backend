CREATE TABLE IF NOT EXISTS air_match_crew (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    match_id uuid NOT NULL REFERENCES brkt_matches(id),
    invited_by uuid NOT NULL REFERENCES auth.users(id),
    recipient_id uuid NOT NULL REFERENCES auth.users(id),
    role text NOT NULL CHECK(role IN ('producer','technical_director','observer','monitor','replay_operator')),
    can_submit_result boolean NOT NULL DEFAULT false,
    status text NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','accepted','revoked')),
    expires_at timestamptz NOT NULL DEFAULT now()+interval '72 hours',
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(match_id,recipient_id)
);
ALTER TABLE air_match_crew ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON air_match_crew FROM anon,authenticated;
