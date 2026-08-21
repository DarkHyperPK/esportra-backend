-- profiles.is_verified was never maintained by the .NET backend — always false for every user.
-- Real verification state lives in: auth.users.email_confirmed_at (platform) and verified_roles (organizer/venue_owner).
ALTER TABLE public.profiles DROP COLUMN IF EXISTS is_verified;
