-- Atomic RPC: file a match result dispute.
-- Replaces the fragile 2-step UPDATE+INSERT in useMatchResultReport.disputeReport().
-- Writes to BOTH match_disputes AND tournament_disputes (so the organizer's
-- dispute tab in /organizer/tournament/:slug?tab=disputes shows it),
-- then fires notifications to the original reporter and the tournament organizer.

CREATE OR REPLACE FUNCTION public.file_match_result_dispute(
    p_report_id   UUID,
    p_match_id    UUID,
    p_team_id     UUID,
    p_reason      TEXT
)
RETURNS VOID
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
DECLARE
    v_tournament_id   UUID;
    v_organizer_id    UUID;
    v_reporter_id     UUID;
    v_tournament_slug TEXT;
BEGIN
    -- Resolve tournament + reporter from report → match → version → tournament
    SELECT v.tournament_id, t.organizer_id, r.reported_by, t.slug
    INTO   v_tournament_id, v_organizer_id, v_reporter_id, v_tournament_slug
    FROM   public.match_result_reports r
    JOIN   public.brkt_matches          m ON m.id = r.match_id
    JOIN   public.brkt_versions         v ON v.id = m.version_id
    JOIN   public.tournaments           t ON t.id = v.tournament_id
    WHERE  r.id = p_report_id;

    -- 1. Mark the result report as disputed
    UPDATE public.match_result_reports
    SET    status         = 'disputed',
           responded_by   = auth.uid(),
           responded_at   = NOW(),
           dispute_reason = p_reason
    WHERE  id = p_report_id;

    -- 2. Create match-level dispute entry (used by useMatchDispute hook)
    INSERT INTO public.match_disputes
        (match_id, disputed_by_team_id, disputed_by_user_id, reason, evidence_urls, status)
    VALUES
        (p_match_id, p_team_id, auth.uid(), p_reason, '{}', 'pending');

    -- 3. Create tournament-level dispute entry (visible in tournament's disputes tab)
    INSERT INTO public.tournament_disputes
        (tournament_id, match_id, raised_by_user_id, team_id,
         title, description, status, dispute_reason)
    VALUES
        (v_tournament_id, p_match_id, auth.uid(), p_team_id,
         'Match Result Disputed', p_reason, 'open', 'result_dispute');

    -- 4a. Notify the reporter that their result is being disputed
    IF v_reporter_id IS NOT NULL THEN
        INSERT INTO public.notifications
            (user_id, type, title, message, link, data, is_read)
        VALUES
            (v_reporter_id, 'result_disputed',
             'Match Result Disputed',
             'The opposing team has disputed your reported result. An organizer will review.',
             '/tournaments/captain',
             jsonb_build_object('match_id', p_match_id),
             false);
    END IF;

    -- 4b. Notify the tournament organizer (link directly to tournament disputes tab)
    IF v_organizer_id IS NOT NULL THEN
        INSERT INTO public.notifications
            (user_id, type, title, message, link, data, is_read)
        VALUES
            (v_organizer_id, 'dispute_filed',
             'Match Dispute Filed',
             'A team has disputed a match result in your tournament. Review in the Disputes tab.',
             '/organizer/tournament/' || COALESCE(v_tournament_slug, v_tournament_id::TEXT) || '?tab=disputes',
             jsonb_build_object('match_id', p_match_id, 'tournament_id', v_tournament_id),
             false);
    END IF;
END;
$$;

GRANT EXECUTE ON FUNCTION public.file_match_result_dispute(UUID, UUID, UUID, TEXT) TO authenticated;
