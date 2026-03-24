-- Backfill license records for existing users
-- venue_owner: any user who owns at least one venue
-- organizer: any user with role = 'organizer'
-- After inserting, link profiles.license_id to their primary license

-- 1. Venue owner licenses (source of truth: venues.owner_id)
INSERT INTO public.licenses (user_id, license_type)
SELECT DISTINCT v.owner_id, 'venue_owner'
FROM public.venues v
WHERE v.owner_id IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 FROM public.licenses l
    WHERE l.user_id = v.owner_id AND l.license_type = 'venue_owner'
  );

-- 2. Organizer licenses (source of truth: profiles.role = 'organizer')
INSERT INTO public.licenses (user_id, license_type)
SELECT p.id, 'organizer'
FROM public.profiles p
WHERE p.role = 'organizer'
  AND NOT EXISTS (
    SELECT 1 FROM public.licenses l
    WHERE l.user_id = p.id AND l.license_type = 'organizer'
  );

-- 3. Link profiles.license_id → their earliest active license
--    Only updates rows where license_id is currently NULL
UPDATE public.profiles p
SET license_id = (
  SELECT l.id
  FROM public.licenses l
  WHERE l.user_id = p.id
    AND l.status = 'active'
  ORDER BY l.issued_at ASC
  LIMIT 1
)
WHERE p.license_id IS NULL
  AND EXISTS (
    SELECT 1 FROM public.licenses l WHERE l.user_id = p.id
  );
