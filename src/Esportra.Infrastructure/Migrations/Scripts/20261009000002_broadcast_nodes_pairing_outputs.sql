-- ============================================================================
-- P0 Broadcast Infrastructure: nodes, pairing codes, and output URLs
-- PROJ-057 (Esportra Air broadcast production system)
--
-- Tables created (all idempotent):
--   public.broadcast_nodes          — paired production PCs registered to an org
--   public.broadcast_pairing_codes  — single-use 10-minute pairing codes
--   public.broadcast_outputs        — per-node overlay browser-source URLs
--
-- Ownership model: organisations have a single owner_id; RLS checks
-- organizations.owner_id = auth.uid() (no organization_members table exists).
-- Raw tokens and codes are NEVER stored — only SHA-256 hashes.
-- ============================================================================

-- ── 1. broadcast_nodes ───────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.broadcast_nodes (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    org_id          UUID        NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    name            TEXT        NOT NULL,
    token_hash      TEXT        NOT NULL,       -- SHA-256 of issued node token; raw never stored
    capabilities    JSONB       NOT NULL DEFAULT '{}',  -- e.g. {"gep": true, "obs": true}
    last_seen_at    TIMESTAMPTZ,
    is_active       BOOLEAN     NOT NULL DEFAULT true,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- token_hash must be unique — used for fast auth lookup on every node heartbeat
CREATE UNIQUE INDEX IF NOT EXISTS broadcast_nodes_token_hash_uniq
    ON public.broadcast_nodes (token_hash);

CREATE INDEX IF NOT EXISTS broadcast_nodes_org_id_idx
    ON public.broadcast_nodes (org_id);

CREATE INDEX IF NOT EXISTS broadcast_nodes_org_active_idx
    ON public.broadcast_nodes (org_id, is_active);

ALTER TABLE public.broadcast_nodes ENABLE ROW LEVEL SECURITY;

-- SELECT: org owner only
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_nodes' AND policyname = 'broadcast_nodes_org_select'
  ) THEN
    CREATE POLICY "broadcast_nodes_org_select" ON public.broadcast_nodes
      FOR SELECT TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_nodes.org_id
            AND owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- INSERT: org owner only
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_nodes' AND policyname = 'broadcast_nodes_org_insert'
  ) THEN
    CREATE POLICY "broadcast_nodes_org_insert" ON public.broadcast_nodes
      FOR INSERT TO authenticated
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_nodes.org_id
            AND owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- UPDATE: org owner only; WITH CHECK mirrors USING to prevent org_id reassignment
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_nodes' AND policyname = 'broadcast_nodes_org_update'
  ) THEN
    CREATE POLICY "broadcast_nodes_org_update" ON public.broadcast_nodes
      FOR UPDATE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_nodes.org_id
            AND owner_id = auth.uid()
        )
      )
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_nodes.org_id
            AND owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- DELETE: org owner only
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_nodes' AND policyname = 'broadcast_nodes_org_delete'
  ) THEN
    CREATE POLICY "broadcast_nodes_org_delete" ON public.broadcast_nodes
      FOR DELETE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_nodes.org_id
            AND owner_id = auth.uid()
        )
      );
  END IF;
END $$;

GRANT SELECT, INSERT, UPDATE, DELETE ON public.broadcast_nodes TO authenticated;
GRANT ALL ON public.broadcast_nodes TO service_role;

-- ── 2. broadcast_pairing_codes ───────────────────────────────────────────────
-- Single-use, 10-minute codes. Created by the web UI; consumed once by the
-- desktop app during the pairing handshake. code_hash is SHA-256 of the
-- 8-char alphanumeric code displayed in the UI.

CREATE TABLE IF NOT EXISTS public.broadcast_pairing_codes (
    id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    org_id              UUID        NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    code_hash           TEXT        NOT NULL UNIQUE,   -- SHA-256 of 8-char code; raw never stored
    created_by          UUID        NOT NULL REFERENCES auth.users(id),
    expires_at          TIMESTAMPTZ NOT NULL DEFAULT (NOW() + INTERVAL '10 minutes'),
    claimed_at          TIMESTAMPTZ,                   -- NULL = unclaimed
    claimed_by_node_id  UUID        REFERENCES public.broadcast_nodes(id),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- (code_hash) unique index is already backed by the inline UNIQUE constraint above.
-- Additional covering indexes for cleanup job and org-scoped queries:
CREATE INDEX IF NOT EXISTS broadcast_pairing_codes_org_id_idx
    ON public.broadcast_pairing_codes (org_id);

CREATE INDEX IF NOT EXISTS broadcast_pairing_codes_expires_at_idx
    ON public.broadcast_pairing_codes (expires_at);

ALTER TABLE public.broadcast_pairing_codes ENABLE ROW LEVEL SECURITY;

-- SELECT: org owner only — code hashes are sensitive; no crew read
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_pairing_codes' AND policyname = 'broadcast_pairing_codes_org_select'
  ) THEN
    CREATE POLICY "broadcast_pairing_codes_org_select" ON public.broadcast_pairing_codes
      FOR SELECT TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_pairing_codes.org_id
            AND owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- INSERT: org owner only
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_pairing_codes' AND policyname = 'broadcast_pairing_codes_org_insert'
  ) THEN
    CREATE POLICY "broadcast_pairing_codes_org_insert" ON public.broadcast_pairing_codes
      FOR INSERT TO authenticated
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_pairing_codes.org_id
            AND owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- DELETE: org owner only — to revoke or clean up expired codes
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_pairing_codes' AND policyname = 'broadcast_pairing_codes_org_delete'
  ) THEN
    CREATE POLICY "broadcast_pairing_codes_org_delete" ON public.broadcast_pairing_codes
      FOR DELETE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_pairing_codes.org_id
            AND owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- No UPDATE policy — codes are immutable after creation; claiming is done
-- via service_role (backend endpoint) to prevent client-side manipulation.

GRANT SELECT, INSERT, DELETE ON public.broadcast_pairing_codes TO authenticated;
GRANT ALL ON public.broadcast_pairing_codes TO service_role;

-- ── 3. broadcast_outputs ─────────────────────────────────────────────────────
-- Per-node browser-source URLs. Each output is a named overlay bundle.
-- key_hash stores SHA-256 of the issued output key; raw key shown once at creation.

CREATE TABLE IF NOT EXISTS public.broadcast_outputs (
    id          UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    node_id     UUID        NOT NULL REFERENCES public.broadcast_nodes(id) ON DELETE CASCADE,
    org_id      UUID        NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    name        TEXT        NOT NULL,           -- e.g. "Player Cards Left"
    output_type TEXT        NOT NULL,           -- e.g. "scoreboard", "minimap", "round_timer"
    key_hash    TEXT        NOT NULL,           -- SHA-256 of output key; raw never stored
    path        TEXT        NOT NULL,           -- URL path, e.g. "/hud/scoreboard"
    is_active   BOOLEAN     NOT NULL DEFAULT true,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- One of each overlay type per node
CREATE UNIQUE INDEX IF NOT EXISTS broadcast_outputs_node_type_uniq
    ON public.broadcast_outputs (node_id, output_type);

-- key_hash used for URL auth — must be unique globally
CREATE UNIQUE INDEX IF NOT EXISTS broadcast_outputs_key_hash_uniq
    ON public.broadcast_outputs (key_hash);

CREATE INDEX IF NOT EXISTS broadcast_outputs_node_id_idx
    ON public.broadcast_outputs (node_id);

CREATE INDEX IF NOT EXISTS broadcast_outputs_org_id_idx
    ON public.broadcast_outputs (org_id);

ALTER TABLE public.broadcast_outputs ENABLE ROW LEVEL SECURITY;

-- SELECT: org owner — crew reads URLs through owner-mediated access
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_outputs' AND policyname = 'broadcast_outputs_org_select'
  ) THEN
    CREATE POLICY "broadcast_outputs_org_select" ON public.broadcast_outputs
      FOR SELECT TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_outputs.org_id
            AND owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- INSERT: org owner only
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_outputs' AND policyname = 'broadcast_outputs_org_insert'
  ) THEN
    CREATE POLICY "broadcast_outputs_org_insert" ON public.broadcast_outputs
      FOR INSERT TO authenticated
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_outputs.org_id
            AND owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- UPDATE: org owner only; WITH CHECK mirrors USING to block org_id reassignment
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_outputs' AND policyname = 'broadcast_outputs_org_update'
  ) THEN
    CREATE POLICY "broadcast_outputs_org_update" ON public.broadcast_outputs
      FOR UPDATE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_outputs.org_id
            AND owner_id = auth.uid()
        )
      )
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_outputs.org_id
            AND owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- DELETE: org owner only
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_outputs' AND policyname = 'broadcast_outputs_org_delete'
  ) THEN
    CREATE POLICY "broadcast_outputs_org_delete" ON public.broadcast_outputs
      FOR DELETE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = broadcast_outputs.org_id
            AND owner_id = auth.uid()
        )
      );
  END IF;
END $$;

GRANT SELECT, INSERT, UPDATE, DELETE ON public.broadcast_outputs TO authenticated;
GRANT ALL ON public.broadcast_outputs TO service_role;
