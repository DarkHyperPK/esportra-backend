-- ============================================================================
-- T24: Broadcast rundown templates and segments
-- PROJ-057 P3 (Esportra Air broadcast production system)
--
-- Tables created (all idempotent):
--   public.rundown_templates  — reusable segment sequences owned by an org
--   public.rundown_segments   — ordered items belonging to exactly one parent
--                               (template_id XOR show_id enforced by CHECK constraint)
--
-- Ownership model: org owner has full CRUD on templates directly (org_id on
-- the table). Segments chain through either rundown_templates or broadcast_shows
-- depending on which parent column is non-null. The single RLS policy per
-- operation covers both paths via an OR clause. Default deny on all tables.
-- ============================================================================

-- ── 1. rundown_templates ──────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.rundown_templates (
    id          UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    org_id      UUID        NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    name        TEXT        NOT NULL,
    description TEXT,
    is_default  BOOLEAN     NOT NULL DEFAULT false,
    created_by  UUID        NOT NULL REFERENCES auth.users(id),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS rundown_templates_org_id_idx
    ON public.rundown_templates (org_id);

-- Template names are unique per org
CREATE UNIQUE INDEX IF NOT EXISTS rundown_templates_org_name_uniq
    ON public.rundown_templates (org_id, name);

ALTER TABLE public.rundown_templates ENABLE ROW LEVEL SECURITY;

-- SELECT: org owner only
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'rundown_templates' AND policyname = 'rundown_templates_org_select'
  ) THEN
    CREATE POLICY "rundown_templates_org_select" ON public.rundown_templates
      FOR SELECT TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = rundown_templates.org_id
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
    WHERE tablename = 'rundown_templates' AND policyname = 'rundown_templates_org_insert'
  ) THEN
    CREATE POLICY "rundown_templates_org_insert" ON public.rundown_templates
      FOR INSERT TO authenticated
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = rundown_templates.org_id
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
    WHERE tablename = 'rundown_templates' AND policyname = 'rundown_templates_org_update'
  ) THEN
    CREATE POLICY "rundown_templates_org_update" ON public.rundown_templates
      FOR UPDATE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = rundown_templates.org_id
            AND owner_id = auth.uid()
        )
      )
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = rundown_templates.org_id
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
    WHERE tablename = 'rundown_templates' AND policyname = 'rundown_templates_org_delete'
  ) THEN
    CREATE POLICY "rundown_templates_org_delete" ON public.rundown_templates
      FOR DELETE TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM organizations
          WHERE id = rundown_templates.org_id
            AND owner_id = auth.uid()
        )
      );
  END IF;
END $$;

GRANT SELECT, INSERT, UPDATE, DELETE ON public.rundown_templates TO authenticated;
GRANT ALL ON public.rundown_templates TO service_role;

-- ── 2. rundown_segments ───────────────────────────────────────────────────────
-- Belongs to exactly one parent: either a rundown_template or a broadcast_show.
-- The CHECK constraint num_nonnulls(template_id, show_id) = 1 enforces this.
-- RLS uses an OR policy: access is granted when the caller owns the org that
-- owns whichever parent is set.

CREATE TABLE IF NOT EXISTS public.rundown_segments (
    id               UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    template_id      UUID        REFERENCES public.rundown_templates(id) ON DELETE CASCADE,
    show_id          UUID        REFERENCES public.broadcast_shows(id) ON DELETE CASCADE,
    display_order    INTEGER     NOT NULL DEFAULT 0,
    type             TEXT        NOT NULL,   -- match | break | pre_show | post_show | sponsor_slot | custom
    title            TEXT        NOT NULL,
    duration_seconds INTEGER,               -- estimated duration; null = unknown
    config           JSONB       NOT NULL DEFAULT '{}',
    status           TEXT        NOT NULL DEFAULT 'pending',  -- pending | active | done | skipped
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Exactly one parent: template_id XOR show_id must be non-null
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'rundown_segments_one_parent_check'
  ) THEN
    ALTER TABLE public.rundown_segments
      ADD CONSTRAINT rundown_segments_one_parent_check
        CHECK (num_nonnulls(template_id, show_id) = 1);
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS rundown_segments_template_order_idx
    ON public.rundown_segments (template_id, display_order)
    WHERE template_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS rundown_segments_show_order_idx
    ON public.rundown_segments (show_id, display_order)
    WHERE show_id IS NOT NULL;

ALTER TABLE public.rundown_segments ENABLE ROW LEVEL SECURITY;

-- SELECT: org owner via either parent path
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'rundown_segments' AND policyname = 'rundown_segments_org_select'
  ) THEN
    CREATE POLICY "rundown_segments_org_select" ON public.rundown_segments
      FOR SELECT TO authenticated
      USING (
        (
          template_id IS NOT NULL AND EXISTS (
            SELECT 1 FROM rundown_templates rt
            JOIN organizations o ON o.id = rt.org_id
            WHERE rt.id = rundown_segments.template_id
              AND o.owner_id = auth.uid()
          )
        )
        OR
        (
          show_id IS NOT NULL AND EXISTS (
            SELECT 1 FROM broadcast_shows bs
            JOIN organizations o ON o.id = bs.org_id
            WHERE bs.id = rundown_segments.show_id
              AND o.owner_id = auth.uid()
          )
        )
      );
  END IF;
END $$;

-- INSERT: org owner via either parent path
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'rundown_segments' AND policyname = 'rundown_segments_org_insert'
  ) THEN
    CREATE POLICY "rundown_segments_org_insert" ON public.rundown_segments
      FOR INSERT TO authenticated
      WITH CHECK (
        (
          template_id IS NOT NULL AND EXISTS (
            SELECT 1 FROM rundown_templates rt
            JOIN organizations o ON o.id = rt.org_id
            WHERE rt.id = rundown_segments.template_id
              AND o.owner_id = auth.uid()
          )
        )
        OR
        (
          show_id IS NOT NULL AND EXISTS (
            SELECT 1 FROM broadcast_shows bs
            JOIN organizations o ON o.id = bs.org_id
            WHERE bs.id = rundown_segments.show_id
              AND o.owner_id = auth.uid()
          )
        )
      );
  END IF;
END $$;

-- UPDATE: org owner via either parent path; WITH CHECK mirrors USING to prevent
-- parent reassignment across org boundaries
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'rundown_segments' AND policyname = 'rundown_segments_org_update'
  ) THEN
    CREATE POLICY "rundown_segments_org_update" ON public.rundown_segments
      FOR UPDATE TO authenticated
      USING (
        (
          template_id IS NOT NULL AND EXISTS (
            SELECT 1 FROM rundown_templates rt
            JOIN organizations o ON o.id = rt.org_id
            WHERE rt.id = rundown_segments.template_id
              AND o.owner_id = auth.uid()
          )
        )
        OR
        (
          show_id IS NOT NULL AND EXISTS (
            SELECT 1 FROM broadcast_shows bs
            JOIN organizations o ON o.id = bs.org_id
            WHERE bs.id = rundown_segments.show_id
              AND o.owner_id = auth.uid()
          )
        )
      )
      WITH CHECK (
        (
          template_id IS NOT NULL AND EXISTS (
            SELECT 1 FROM rundown_templates rt
            JOIN organizations o ON o.id = rt.org_id
            WHERE rt.id = rundown_segments.template_id
              AND o.owner_id = auth.uid()
          )
        )
        OR
        (
          show_id IS NOT NULL AND EXISTS (
            SELECT 1 FROM broadcast_shows bs
            JOIN organizations o ON o.id = bs.org_id
            WHERE bs.id = rundown_segments.show_id
              AND o.owner_id = auth.uid()
          )
        )
      );
  END IF;
END $$;

-- DELETE: org owner via either parent path
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'rundown_segments' AND policyname = 'rundown_segments_org_delete'
  ) THEN
    CREATE POLICY "rundown_segments_org_delete" ON public.rundown_segments
      FOR DELETE TO authenticated
      USING (
        (
          template_id IS NOT NULL AND EXISTS (
            SELECT 1 FROM rundown_templates rt
            JOIN organizations o ON o.id = rt.org_id
            WHERE rt.id = rundown_segments.template_id
              AND o.owner_id = auth.uid()
          )
        )
        OR
        (
          show_id IS NOT NULL AND EXISTS (
            SELECT 1 FROM broadcast_shows bs
            JOIN organizations o ON o.id = bs.org_id
            WHERE bs.id = rundown_segments.show_id
              AND o.owner_id = auth.uid()
          )
        )
      );
  END IF;
END $$;

GRANT SELECT, INSERT, UPDATE, DELETE ON public.rundown_segments TO authenticated;
GRANT ALL ON public.rundown_segments TO service_role;
