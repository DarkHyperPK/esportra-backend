-- ============================================================================
-- PROJ-057 QA M2: broadcast_corrections — add client_key for drain idempotency
--
-- Purpose:
--   The corrections drain endpoint (POST /api/broadcast/shows/{showId}/corrections)
--   has no idempotency guard. On retry a second identical batch is inserted,
--   inflating corrections.count in the post-show report.
--
--   Fix: nodes include a per-correction UUID in drain requests; the server stores
--   it in client_key and uses ON CONFLICT (client_key) DO NOTHING.
--
--   client_key is nullable — legacy rows without a key are unaffected, and
--   the INSERT falls back to a plain insert (non-idempotent) when omitted.
--   The partial unique index enforces uniqueness only for non-null keys.
-- ============================================================================

ALTER TABLE public.broadcast_corrections
    ADD COLUMN IF NOT EXISTS client_key TEXT;

CREATE UNIQUE INDEX IF NOT EXISTS broadcast_corrections_client_key_idx
    ON public.broadcast_corrections (client_key)
    WHERE client_key IS NOT NULL;
