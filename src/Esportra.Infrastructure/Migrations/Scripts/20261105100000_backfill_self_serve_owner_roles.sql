-- Backfill self-serve owner roles.
--
-- Organizer and venue owner are self-serve: POST /api/organizations grants organizer and
-- POST /api/venues grants venue_owner, both as user_roles + verified_roles (approved) in
-- the same transaction. Organizations and venues created before those endpoints did this
-- left their owners with no role rows, so the owner cannot switch into the role and the
-- frontend hides "Setup Organization" because they already own one.
--
-- Only owners with NO row for the role are granted. An existing row, including one an
-- admin deactivated or rejected, is left untouched, so this never re-enables a revoked role.
-- Inserts only; idempotent via NOT EXISTS + ON CONFLICT DO NOTHING.

-- PRE-FLIGHT: Run on production before deploying, confirm expected count before proceeding:
-- SELECT 'organizer' AS role, count(DISTINCT o.owner_id) FROM public.organizations o
--  WHERE NOT EXISTS (SELECT 1 FROM public.user_roles ur WHERE ur.user_id = o.owner_id AND ur.role = 'organizer')
-- UNION ALL
-- SELECT 'venue_owner', count(DISTINCT v.owner_id) FROM public.venues v
--  WHERE v.deleted_at IS NULL AND v.owner_id IS NOT NULL
--    AND NOT EXISTS (SELECT 1 FROM public.user_roles ur WHERE ur.user_id = v.owner_id AND ur.role = 'venue_owner');
-- Expected: a small number of legacy owners (single digits to low tens).

-- 1. Organization owners → user_roles
INSERT INTO public.user_roles (user_id, role, is_active)
SELECT DISTINCT o.owner_id, 'organizer', TRUE
FROM public.organizations o
WHERE NOT EXISTS (
    SELECT 1 FROM public.user_roles ur
    WHERE ur.user_id = o.owner_id AND ur.role = 'organizer'
)
ON CONFLICT (user_id, role) DO NOTHING;

-- 2. Organization owners → verified_roles
INSERT INTO public.verified_roles (user_id, role, status, is_active, verified_at)
SELECT DISTINCT o.owner_id, 'organizer'::app_role, 'approved', TRUE, NOW()
FROM public.organizations o
WHERE NOT EXISTS (
    SELECT 1 FROM public.verified_roles vr
    WHERE vr.user_id = o.owner_id AND vr.role = 'organizer'::app_role
)
ON CONFLICT (user_id, role) DO NOTHING;

-- 3. Venue owners → user_roles
INSERT INTO public.user_roles (user_id, role, is_active)
SELECT DISTINCT v.owner_id, 'venue_owner', TRUE
FROM public.venues v
WHERE v.deleted_at IS NULL
  AND v.owner_id IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 FROM public.user_roles ur
    WHERE ur.user_id = v.owner_id AND ur.role = 'venue_owner'
  )
ON CONFLICT (user_id, role) DO NOTHING;

-- 4. Venue owners → verified_roles
INSERT INTO public.verified_roles (user_id, role, status, is_active, verified_at)
SELECT DISTINCT v.owner_id, 'venue_owner'::app_role, 'approved', TRUE, NOW()
FROM public.venues v
WHERE v.deleted_at IS NULL
  AND v.owner_id IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 FROM public.verified_roles vr
    WHERE vr.user_id = v.owner_id AND vr.role = 'venue_owner'::app_role
  )
ON CONFLICT (user_id, role) DO NOTHING;
