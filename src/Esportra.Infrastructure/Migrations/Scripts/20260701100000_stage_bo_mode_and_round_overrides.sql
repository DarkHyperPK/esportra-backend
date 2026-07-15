-- Migration: Add per-round BO format support for elimination brackets
-- Adds bo_mode toggle and round_bo_overrides JSONB column to tournament_stages

-- BO mode: per_stage (default, current behavior) or per_round (new)
ALTER TABLE public.tournament_stages
ADD COLUMN IF NOT EXISTS bo_mode TEXT NOT NULL DEFAULT 'per_stage'
    CHECK (bo_mode IN ('per_stage', 'per_round'));

-- Per-round BO overrides (only used when bo_mode = 'per_round')
ALTER TABLE public.tournament_stages
ADD COLUMN IF NOT EXISTS round_bo_overrides JSONB DEFAULT NULL;

COMMENT ON COLUMN public.tournament_stages.bo_mode IS
'BO format mode: per_stage (uniform BO for all matches) or per_round (different BO per round, elimination formats only).';

COMMENT ON COLUMN public.tournament_stages.round_bo_overrides IS
'Per-round BO format overrides when bo_mode = per_round. Keys: final, semifinals, quarterfinals, losers_final, default. Values: 1, 3, or 5.';
