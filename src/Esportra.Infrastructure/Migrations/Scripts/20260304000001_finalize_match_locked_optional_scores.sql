-- Make p_team1_score and p_team2_score optional (DEFAULT NULL) in finalize_match_locked.
--
-- The process-match-result edge function calls this with 4 params (no scores) because
-- it tracks per-map scores in brkt_match_games separately.
-- The bracket UI callers (GraphBracket, ManualAdjustmentMenu) call with 6 params.
-- Making scores optional with COALESCE preserves existing score values when not supplied.

CREATE OR REPLACE FUNCTION public.finalize_match_locked(
    p_match_id          UUID,
    p_expected_version  INTEGER,
    p_winner_id         UUID,
    p_loser_id          UUID,
    p_team1_score       INTEGER DEFAULT NULL,
    p_team2_score       INTEGER DEFAULT NULL
)
RETURNS BOOLEAN
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
DECLARE
    v_actual_version  INTEGER;
    v_tournament_id   UUID;
    v_is_authorized   BOOLEAN;
BEGIN
    -- 1. Pessimistic Lock
    SELECT version, v.tournament_id
    INTO v_actual_version, v_tournament_id
    FROM public.brkt_matches m
    JOIN public.brkt_versions v ON m.version_id = v.id
    WHERE m.id = p_match_id
    FOR UPDATE;

    IF v_actual_version IS NULL THEN
        RETURN FALSE;
    END IF;

    -- 2. Version Check
    IF v_actual_version != p_expected_version THEN
        RAISE EXCEPTION 'Match version mismatch. Expected %, got %', p_expected_version, v_actual_version;
    END IF;

    -- 3. Authorization Check
    SELECT EXISTS (
        SELECT 1 FROM public.profiles p
        WHERE p.id = auth.uid()
          AND (p.is_admin = TRUE OR p.role = 'admin')
    ) OR EXISTS (
        SELECT 1 FROM public.tournaments t
        WHERE t.id = v_tournament_id
          AND (
            t.organizer_id = auth.uid()
            OR t.organization_id IN (SELECT id FROM organizations WHERE owner_id = auth.uid())
          )
    ) INTO v_is_authorized;

    -- Edge Functions run as service role (auth.uid() is NULL); allow them through.
    IF auth.uid() IS NOT NULL AND NOT v_is_authorized THEN
        RAISE EXCEPTION 'Unauthorized to finalize this match';
    END IF;

    -- 4. Atomic Score Update
    -- When scores are not supplied (edge function path), keep existing values via COALESCE.
    UPDATE public.brkt_matches
    SET
        winner_id   = p_winner_id,
        loser_id    = p_loser_id,
        team1_score = COALESCE(p_team1_score, team1_score),
        team2_score = COALESCE(p_team2_score, team2_score),
        status      = 'completed',
        version     = version + 1,
        updated_at  = now()
    WHERE id = p_match_id;

    -- 5. Instant Database-Side Progression
    PERFORM public.proc_internal_advance_match(p_match_id, p_winner_id, p_loser_id);

    -- 6. External Notify Event
    INSERT INTO public.match_completed_events (match_id, winner_id, loser_id, status)
    VALUES (p_match_id, p_winner_id, p_loser_id, 'processed');

    RETURN TRUE;
END;
$$;
