-- broadcast_overlay_tokens: short-lived show-scoped read-only tokens for browser source overlays.
-- A browser source connects to BroadcastHub with ?overlay_token=<raw>, which is verified by
-- hashing and looking up here. Supabase JWTs are never used in browser source URLs (CIO C-1).

CREATE TABLE IF NOT EXISTS broadcast_overlay_tokens (
    id          uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    show_id     uuid        NOT NULL REFERENCES broadcast_shows(id) ON DELETE CASCADE,
    org_id      uuid        NOT NULL REFERENCES organizations(id)   ON DELETE CASCADE,
    token_hash  text        NOT NULL,
    label       text,
    created_at  timestamptz NOT NULL DEFAULT now(),
    expires_at  timestamptz NOT NULL,
    revoked_at  timestamptz,
    last_ping_at timestamptz
);

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'broadcast_overlay_tokens_token_hash_key'
  ) THEN
    ALTER TABLE broadcast_overlay_tokens ADD CONSTRAINT broadcast_overlay_tokens_token_hash_key UNIQUE (token_hash);
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_broadcast_overlay_tokens_show
    ON broadcast_overlay_tokens(show_id);

CREATE INDEX IF NOT EXISTS idx_broadcast_overlay_tokens_active
    ON broadcast_overlay_tokens(token_hash)
    WHERE revoked_at IS NULL;

ALTER TABLE broadcast_overlay_tokens ENABLE ROW LEVEL SECURITY;

-- Service role bypasses RLS; all user roles are denied.
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_overlay_tokens' AND policyname = 'overlay_tokens_deny_all'
  ) THEN
    CREATE POLICY overlay_tokens_deny_all ON broadcast_overlay_tokens FOR ALL USING (false);
  END IF;
END $$;
