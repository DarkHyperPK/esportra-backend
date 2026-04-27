-- ============================================================================
-- Migration: BR Round Integrity Guards
-- Purpose:   Enforce critical BR invariants at the database layer so the app
--            cannot persist split-brain active rounds or duplicate placements.
-- ============================================================================

-- Normalize any historic duplicate active rounds before enforcing uniqueness.
-- This migration predates br_round_evidence, so production databases that have
-- not reached the later evidence migration yet must skip evidence references.
DO $$
BEGIN
    IF to_regclass('public.br_round_evidence') IS NOT NULL THEN
        EXECUTE $sql$
            WITH ranked_active_rounds AS (
                SELECT
                    r.id,
                    r.group_id,
                    ROW_NUMBER() OVER (
                        PARTITION BY r.group_id
                        ORDER BY COALESCE(r.queue_started_at, r.started_at, r.created_at) DESC NULLS LAST,
                                 r.round_number DESC
                    ) AS active_rank,
                    EXISTS (SELECT 1 FROM br_round_results rr WHERE rr.round_id = r.id) AS has_results,
                    EXISTS (SELECT 1 FROM br_round_evidence re WHERE re.round_id = r.id) AS has_evidence
                FROM br_rounds r
                WHERE r.status = 'active'
            )
            UPDATE br_rounds r
            SET status = CASE
                             WHEN ranked_active_rounds.has_results OR ranked_active_rounds.has_evidence THEN 'completed'
                             ELSE 'pending'
                         END,
                started_at = CASE
                                 WHEN ranked_active_rounds.has_results OR ranked_active_rounds.has_evidence THEN r.started_at
                                 ELSE NULL
                             END,
                completed_at = CASE
                                   WHEN ranked_active_rounds.has_results OR ranked_active_rounds.has_evidence THEN COALESCE(r.completed_at, NOW())
                                   ELSE NULL
                               END,
                queue_started_at = NULL
            FROM ranked_active_rounds
            WHERE r.id = ranked_active_rounds.id
              AND ranked_active_rounds.active_rank > 1;
        $sql$;
    ELSE
        WITH ranked_active_rounds AS (
            SELECT
                r.id,
                r.group_id,
                ROW_NUMBER() OVER (
                    PARTITION BY r.group_id
                    ORDER BY COALESCE(r.queue_started_at, r.started_at, r.created_at) DESC NULLS LAST,
                             r.round_number DESC
                ) AS active_rank,
                EXISTS (SELECT 1 FROM br_round_results rr WHERE rr.round_id = r.id) AS has_results,
                FALSE AS has_evidence
            FROM br_rounds r
            WHERE r.status = 'active'
        )
        UPDATE br_rounds r
        SET status = CASE
                         WHEN ranked_active_rounds.has_results OR ranked_active_rounds.has_evidence THEN 'completed'
                         ELSE 'pending'
                     END,
            started_at = CASE
                             WHEN ranked_active_rounds.has_results OR ranked_active_rounds.has_evidence THEN r.started_at
                             ELSE NULL
                         END,
            completed_at = CASE
                               WHEN ranked_active_rounds.has_results OR ranked_active_rounds.has_evidence THEN COALESCE(r.completed_at, NOW())
                               ELSE NULL
                           END,
            queue_started_at = NULL
        FROM ranked_active_rounds
        WHERE r.id = ranked_active_rounds.id
          AND ranked_active_rounds.active_rank > 1;
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS uq_br_rounds_active_group
    ON br_rounds (group_id)
    WHERE status = 'active';

CREATE UNIQUE INDEX IF NOT EXISTS uq_br_round_results_round_placement
    ON br_round_results (round_id, placement);

DO $$
BEGIN
    IF to_regclass('public.br_round_evidence') IS NOT NULL THEN
        EXECUTE '
            CREATE INDEX IF NOT EXISTS idx_br_round_evidence_pending_review
                ON br_round_evidence (round_id)
                WHERE reviewed = FALSE;
        ';
    END IF;
END $$;
