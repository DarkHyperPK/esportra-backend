-- Drop unused verification columns from profiles table
-- Email verification is handled by Supabase Auth (auth.users.email_confirmed_at)
-- Password reset is handled by Supabase Auth (auth.users.recovery_token)
-- admin_permissions array is replaced by normalized admin_role_permissions table
ALTER TABLE profiles DROP COLUMN IF EXISTS email_verified;
ALTER TABLE profiles DROP COLUMN IF EXISTS email_verification_token;
ALTER TABLE profiles DROP COLUMN IF EXISTS password_reset_token;
ALTER TABLE profiles DROP COLUMN IF EXISTS password_reset_expires;
ALTER TABLE profiles DROP COLUMN IF EXISTS admin_permissions;
