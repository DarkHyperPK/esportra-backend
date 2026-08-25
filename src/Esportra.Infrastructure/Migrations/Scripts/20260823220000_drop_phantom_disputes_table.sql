-- Drop the phantom `disputes` table (Admin Centre V2, Phase 3).
-- It was created speculatively for the command centre but never written to —
-- real disputes live in `tournament_disputes`. All readers were repointed in Phase 1.

DROP TABLE IF EXISTS public.disputes;
