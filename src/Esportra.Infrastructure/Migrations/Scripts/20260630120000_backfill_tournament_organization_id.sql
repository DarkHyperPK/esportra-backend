-- Backfill tournaments.organization_id when organizer owns an organization but FK was never set.
UPDATE tournaments t
SET organization_id = o.id,
    updated_at = NOW()
FROM organizations o
WHERE t.organization_id IS NULL
  AND t.organizer_id = o.owner_id
  AND t.deleted_at IS NULL;
