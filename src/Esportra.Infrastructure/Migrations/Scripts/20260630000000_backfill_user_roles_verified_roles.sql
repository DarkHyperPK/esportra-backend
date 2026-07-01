-- Backfill user_roles and verified_roles for existing organizers and venue_owners.
-- These users may have profiles.role set but no corresponding rows in the
-- normalized RBAC tables (user_roles + verified_roles) that the middleware reads.
-- Safe: ON CONFLICT DO NOTHING ensures idempotency.

-- 1. Organizers → user_roles
INSERT INTO public.user_roles (user_id, role, is_active)
SELECT p.id, 'organizer', TRUE
FROM public.profiles p
WHERE p.role = 'organizer'::app_role
  AND NOT EXISTS (
    SELECT 1 FROM public.user_roles ur
    WHERE ur.user_id = p.id AND ur.role = 'organizer'
  )
ON CONFLICT (user_id, role) DO NOTHING;

-- 2. Organizers → verified_roles (mark as approved since they were already operating)
INSERT INTO public.verified_roles (user_id, role, status, is_active, verified_at)
SELECT p.id, 'organizer', 'approved', TRUE, NOW()
FROM public.profiles p
WHERE p.role = 'organizer'::app_role
  AND NOT EXISTS (
    SELECT 1 FROM public.verified_roles vr
    WHERE vr.user_id = p.id AND vr.role = 'organizer'
  )
ON CONFLICT (user_id, role) DO NOTHING;

-- 3. Venue owners → user_roles
INSERT INTO public.user_roles (user_id, role, is_active)
SELECT p.id, 'venue_owner', TRUE
FROM public.profiles p
WHERE p.role = 'venue_owner'::app_role
  AND NOT EXISTS (
    SELECT 1 FROM public.user_roles ur
    WHERE ur.user_id = p.id AND ur.role = 'venue_owner'
  )
ON CONFLICT (user_id, role) DO NOTHING;

-- 4. Venue owners → verified_roles
INSERT INTO public.verified_roles (user_id, role, status, is_active, verified_at)
SELECT p.id, 'venue_owner', 'approved', TRUE, NOW()
FROM public.profiles p
WHERE p.role = 'venue_owner'::app_role
  AND NOT EXISTS (
    SELECT 1 FROM public.verified_roles vr
    WHERE vr.user_id = p.id AND vr.role = 'venue_owner'
  )
ON CONFLICT (user_id, role) DO NOTHING;
