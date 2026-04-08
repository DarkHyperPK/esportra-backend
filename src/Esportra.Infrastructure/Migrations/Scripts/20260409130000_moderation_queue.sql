-- ============================================================================
-- Migration: Content Moderation Queue
-- Purpose:   Lightweight moderation system for user-generated content
-- ============================================================================

-- ── Table ────────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS moderation_queue (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    content_type    TEXT NOT NULL,                               -- 'tournament', 'team', 'profile', 'match_evidence'
    content_id      UUID NOT NULL,                               -- ID of the entity being moderated
    field_name      TEXT NOT NULL DEFAULT '',                     -- 'name', 'description', 'bio', 'username'
    content_text    TEXT,                                         -- The actual text content to review
    content_url     TEXT,                                         -- URL if it's an image/media
    reported_by     UUID REFERENCES auth.users(id),              -- NULL for auto-flagged items
    reported_reason TEXT DEFAULT '',                              -- User-provided reason
    status          TEXT NOT NULL DEFAULT 'pending',              -- 'pending', 'approved', 'rejected'
    reviewed_by     UUID REFERENCES auth.users(id),
    reviewed_at     TIMESTAMPTZ,
    review_notes    TEXT DEFAULT '',
    auto_flagged    BOOLEAN NOT NULL DEFAULT FALSE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ── RLS ──────────────────────────────────────────────────────────────────────

ALTER TABLE moderation_queue ENABLE ROW LEVEL SECURITY;

-- service_role: full access (used by backend with service key)
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'moderation_queue_service_role_all' AND tablename = 'moderation_queue'
  ) THEN
    CREATE POLICY moderation_queue_service_role_all ON moderation_queue
      FOR ALL TO service_role USING (true) WITH CHECK (true);
  END IF;
END; $$;

-- Admins can read all items (check via admin_user_roles)
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'moderation_queue_admin_read' AND tablename = 'moderation_queue'
  ) THEN
    CREATE POLICY moderation_queue_admin_read ON moderation_queue
      FOR SELECT TO authenticated USING (
        EXISTS (SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid())
      );
  END IF;
END; $$;

-- Admins can update items (review actions)
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'moderation_queue_admin_update' AND tablename = 'moderation_queue'
  ) THEN
    CREATE POLICY moderation_queue_admin_update ON moderation_queue
      FOR UPDATE TO authenticated USING (
        EXISTS (SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid())
      ) WITH CHECK (
        EXISTS (SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid())
      );
  END IF;
END; $$;

-- Admins can delete items (dismiss from queue)
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'moderation_queue_admin_delete' AND tablename = 'moderation_queue'
  ) THEN
    CREATE POLICY moderation_queue_admin_delete ON moderation_queue
      FOR DELETE TO authenticated USING (
        EXISTS (SELECT 1 FROM admin_user_roles WHERE user_id = auth.uid())
      );
  END IF;
END; $$;

-- Authenticated users can INSERT (to report content)
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'moderation_queue_authenticated_insert' AND tablename = 'moderation_queue'
  ) THEN
    CREATE POLICY moderation_queue_authenticated_insert ON moderation_queue
      FOR INSERT TO authenticated WITH CHECK (true);
  END IF;
END; $$;

-- ── Grants ───────────────────────────────────────────────────────────────────

GRANT ALL ON moderation_queue TO service_role;
GRANT SELECT, INSERT ON moderation_queue TO authenticated;

-- ── Indexes ──────────────────────────────────────────────────────────────────

CREATE INDEX IF NOT EXISTS idx_moderation_queue_status_created
    ON moderation_queue (status, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_moderation_queue_content
    ON moderation_queue (content_type, content_id);

CREATE INDEX IF NOT EXISTS idx_moderation_queue_reporter
    ON moderation_queue (reported_by)
    WHERE reported_by IS NOT NULL;
