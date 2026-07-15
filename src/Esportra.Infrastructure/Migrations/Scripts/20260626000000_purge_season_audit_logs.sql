-- Purge historical audit rows for the removed season feature.
-- Idempotent — no-op when no season audit rows remain.

DELETE FROM public.audit_logs
WHERE lower(target_type) = 'season';

DELETE FROM public.staff_audit_log
WHERE lower(target_type) = 'season';

-- Remove audit rows that reference season entities in JSON details (orphaned history).
DELETE FROM public.audit_logs
WHERE details ?| ARRAY['seasonId', 'season_id', 'seasonNodeId', 'season_node_id'];
