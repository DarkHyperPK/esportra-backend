-- Fix existing staff records: activate any org staff who have tournament assignments but are still pending.
-- These staff were assigned by organizers and should be active immediately.

UPDATE organization_staff
SET status = 'active',
    accepted_at = COALESCE(accepted_at, NOW()),
    updated_at = NOW()
WHERE status = 'pending'
  AND EXISTS (
      SELECT 1 FROM staff_tournament_assignments sta
      WHERE sta.organization_staff_id = organization_staff.id
  );
