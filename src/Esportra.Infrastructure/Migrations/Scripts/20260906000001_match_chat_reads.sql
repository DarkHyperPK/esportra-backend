-- Per-participant read tracking for match room chat (waterline / last-seen pattern).
-- Max 2 rows per match (one per side). Updated via INSERT ... ON CONFLICT ... DO UPDATE
-- at the app layer -- no SQL UPDATE needed, so no UPDATE policy is created.
-- Receipts are permanent -- no DELETE policy.

CREATE TABLE IF NOT EXISTS public.match_chat_reads (
    match_id     uuid        NOT NULL REFERENCES public.brkt_matches(id) ON DELETE CASCADE,
    user_id      uuid        NOT NULL REFERENCES public.profiles(id)     ON DELETE CASCADE,
    last_read_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (match_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_match_chat_reads_user_id
    ON public.match_chat_reads (user_id);

ALTER TABLE public.match_chat_reads ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.match_chat_reads FORCE ROW LEVEL SECURITY;

REVOKE ALL ON public.match_chat_reads FROM anon, authenticated;
GRANT ALL ON public.match_chat_reads TO service_role;

-- Service role bypass (internal jobs / server-side writes)
DROP POLICY IF EXISTS match_chat_reads_service_role_all ON public.match_chat_reads;
CREATE POLICY match_chat_reads_service_role_all ON public.match_chat_reads
    FOR ALL TO service_role
    USING (true)
    WITH CHECK (true);

-- INSERT (and upsert DO UPDATE) -- only the receipt owner AND a match participant may write
DROP POLICY IF EXISTS match_chat_reads_owner_insert ON public.match_chat_reads;
CREATE POLICY match_chat_reads_owner_insert ON public.match_chat_reads
    FOR INSERT TO authenticated
    WITH CHECK (
        user_id = auth.uid()
        AND (
            -- Path A: team tournament participant
            EXISTS (
                SELECT 1
                FROM public.brkt_matches bm
                JOIN public.team_members tm
                  ON (tm.team_id = bm.team1_id OR tm.team_id = bm.team2_id)
                WHERE bm.id = match_chat_reads.match_id
                  AND tm.user_id = auth.uid()
            )
            OR
            -- Path B: solo tournament participant
            EXISTS (
                SELECT 1
                FROM public.brkt_matches bm
                JOIN public.tournament_participants tp
                  ON (tp.id = bm.team1_id OR tp.id = bm.team2_id)
                WHERE bm.id = match_chat_reads.match_id
                  AND tp.user_id = auth.uid()
            )
        )
    );

-- SELECT -- restricted to match participants only.
-- Dual-path mirrors CanAccessMatchRoom / match_checkins RLS:
--   Path A: team-based tournament -- caller is a member of team1 or team2
--   Path B: solo tournament -- caller is the tournament_participant whose id
--            occupies team1_id or team2_id in the bracket slot
DROP POLICY IF EXISTS match_chat_reads_participant_select ON public.match_chat_reads;
CREATE POLICY match_chat_reads_participant_select ON public.match_chat_reads
    FOR SELECT TO authenticated
    USING (
        -- Path A: team tournament participant
        EXISTS (
            SELECT 1
            FROM public.brkt_matches bm
            JOIN public.team_members tm
              ON (tm.team_id = bm.team1_id OR tm.team_id = bm.team2_id)
            WHERE bm.id = match_chat_reads.match_id
              AND tm.user_id = auth.uid()
        )
        OR
        -- Path B: solo tournament participant
        EXISTS (
            SELECT 1
            FROM public.brkt_matches bm
            JOIN public.tournament_participants tp
              ON (tp.id = bm.team1_id OR tp.id = bm.team2_id)
            WHERE bm.id = match_chat_reads.match_id
              AND tp.user_id = auth.uid()
        )
    );
