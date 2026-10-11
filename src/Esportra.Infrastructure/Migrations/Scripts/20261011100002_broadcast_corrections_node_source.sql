-- ============================================================================
-- T-DB-2: broadcast_corrections — add source, node_id; make corrected_by nullable
-- PROJ-059 P4 (Esportra Air: Results + Reports)
--
-- Purpose:
--   Desktop nodes (Esportra Air) drain FIX_DATA corrections via an HTTP
--   endpoint authenticated with a node bearer token.  Nodes do not have
--   auth.users rows, so corrected_by must become nullable and a node_id
--   foreign key is needed to record which node submitted the correction.
--
--   source distinguishes web-originated corrections (org owner, crew) from
--   node-originated corrections (Desktop node offline queue drain).
--
-- Changes (all idempotent):
--   1. ADD COLUMN source TEXT NOT NULL DEFAULT 'web'
--   2. ADD COLUMN node_id UUID REFERENCES broadcast_nodes(id) ON DELETE SET NULL
--   3. ALTER COLUMN corrected_by DROP NOT NULL (guarded)
--   4. Index on node_id for lookups
-- ============================================================================

-- 1. source column
ALTER TABLE public.broadcast_corrections
    ADD COLUMN IF NOT EXISTS source TEXT NOT NULL DEFAULT 'web';

-- 2. node_id column
ALTER TABLE public.broadcast_corrections
    ADD COLUMN IF NOT EXISTS node_id UUID
        REFERENCES public.broadcast_nodes(id) ON DELETE SET NULL;

-- 3. Make corrected_by nullable — nodes do not have auth.users rows
DO $$
BEGIN
  IF EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name   = 'broadcast_corrections'
      AND column_name  = 'corrected_by'
      AND is_nullable  = 'NO'
  ) THEN
    ALTER TABLE public.broadcast_corrections
      ALTER COLUMN corrected_by DROP NOT NULL;
  END IF;
END $$;

-- 4. Index for node_id (partial — only rows that actually have a node_id)
CREATE INDEX IF NOT EXISTS broadcast_corrections_node_id_idx
    ON public.broadcast_corrections (node_id)
    WHERE node_id IS NOT NULL;
