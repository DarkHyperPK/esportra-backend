-- ============================================================================
-- T-DB-2: Add can_submit_result to broadcast_crew
-- PROJ-059 P4 (Esportra Air Results + Reports)
--
-- Adds a boolean permission flag that the org owner sets per crew member to
-- control who may submit match results through the broadcast interface.
--
-- Default is false (least privilege): newly added crew members have no result
-- submission access until the org owner explicitly grants it.
--
-- RLS: the existing broadcast_crew_org_update policy (org owner, USING + WITH
-- CHECK ownership chain) already covers UPDATE of all columns on this table,
-- including the new can_submit_result column. No new policy is required.
-- ============================================================================

ALTER TABLE public.broadcast_crew
    ADD COLUMN IF NOT EXISTS can_submit_result BOOLEAN NOT NULL DEFAULT false;
