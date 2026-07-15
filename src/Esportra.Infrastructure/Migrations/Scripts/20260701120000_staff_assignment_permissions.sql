-- Per-tournament staff permission overrides (NULL = inherit organization_staff.permissions).
ALTER TABLE staff_tournament_assignments
  ADD COLUMN IF NOT EXISTS permissions text[] NULL;

COMMENT ON COLUMN staff_tournament_assignments.permissions IS
  'NULL = inherit organization_staff.permissions; non-null = effective permissions for this tournament';
