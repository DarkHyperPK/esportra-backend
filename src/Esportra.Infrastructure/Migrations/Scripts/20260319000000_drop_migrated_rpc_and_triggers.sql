-- Drop DB RPCs and triggers that have been fully migrated to .NET backend.
-- These are no longer called by the frontend or any edge function.

-- 1. Drop match-ready notification trigger (replaced by MatchFinalizationService + BracketPersistenceService)
DROP TRIGGER IF EXISTS trg_match_ready_notify ON public.brkt_matches;
DROP FUNCTION IF EXISTS public.notify_match_ready();

-- 2. Drop file_match_result_dispute RPC (replaced by POST /api/matches/{id}/reports/{rid}/dispute)
DROP FUNCTION IF EXISTS public.file_match_result_dispute(UUID, UUID, UUID, TEXT);

-- 3. Drop notify_admins_of_dispute RPC (replaced by POST /api/disputes/notify-admins)
DROP FUNCTION IF EXISTS public.notify_admins_of_dispute(UUID, TEXT, TEXT, TEXT, TEXT);

-- 4. Drop finalize_match_locked RPC (replaced by MatchFinalizationService.cs)
DROP FUNCTION IF EXISTS public.finalize_match_locked(UUID, INT, UUID, UUID, INT, INT);

-- 5. Drop internal advance match procedure (replaced by MatchFinalizationService.AdvanceTeamInternalAsync)
DROP FUNCTION IF EXISTS public.proc_internal_advance_match(UUID, UUID, UUID);
