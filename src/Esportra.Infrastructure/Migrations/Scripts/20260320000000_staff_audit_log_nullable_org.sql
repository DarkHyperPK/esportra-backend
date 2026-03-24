-- Allow platform-level admin audit logs without an organization_id
ALTER TABLE staff_audit_log ALTER COLUMN organization_id DROP NOT NULL;
