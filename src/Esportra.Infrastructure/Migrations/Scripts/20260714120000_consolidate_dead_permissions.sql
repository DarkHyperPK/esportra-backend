-- Consolidate dead paired permissions into their surviving counterparts.
-- Dead permissions were never checked in endpoint handlers; the "positive" permission
-- already gates both directions (e.g., users:ban covers suspend/unsuspend/ban/unban).

-- Step 1: For any role that has a dead permission but NOT the survivor,
-- grant the survivor (prevents accidental access loss during migration).
DO $$
DECLARE
  pair RECORD;
BEGIN
  FOR pair IN
    SELECT dead.name AS dead_name, survivor.id AS survivor_id
    FROM (VALUES
      ('users:suspend',              'users:ban'),
      ('users:unsuspend',            'users:ban'),
      ('users:unban',                'users:ban'),
      ('brackets:unlock',            'brackets:lock'),
      ('matches:unlock',             'matches:lock'),
      ('venues:unpublish',           'venues:publish'),
      ('venues:reject',              'venues:approve'),
      ('venue_stations:unlock',      'venue_stations:lock'),
      ('registrations:reject',       'registrations:approve'),
      ('members:unban',              'members:ban'),
      ('wallets:unfreeze',           'wallets:freeze'),
      ('moderation:reject',          'moderation:approve'),
      ('moderation:dismiss',         'moderation:approve'),
      ('verification:reject',        'verification:approve'),
      ('sponsors:reject_application','sponsors:approve_application'),
      ('tournaments:reject',         'tournaments:approve'),
      ('tournaments:unfeature',      'tournaments:feature'),
      ('organizations:suspend',      NULL)
    ) AS mapping(dead_perm, survivor_perm)
    JOIN admin_permissions dead ON dead.name = mapping.dead_perm
    LEFT JOIN admin_permissions survivor ON survivor.name = mapping.survivor_perm
    WHERE mapping.survivor_perm IS NOT NULL
  LOOP
    INSERT INTO admin_role_permissions (role_id, permission_id)
    SELECT arp.role_id, pair.survivor_id
    FROM admin_role_permissions arp
    JOIN admin_permissions ap ON ap.id = arp.permission_id
    WHERE ap.name = pair.dead_name
      AND NOT EXISTS (
        SELECT 1 FROM admin_role_permissions existing
        WHERE existing.role_id = arp.role_id
          AND existing.permission_id = pair.survivor_id
      )
    ON CONFLICT DO NOTHING;
  END LOOP;
END $$;

-- Step 2: Delete role-permission mappings for dead permissions.
DELETE FROM admin_role_permissions
WHERE permission_id IN (
  SELECT id FROM admin_permissions
  WHERE name IN (
    'users:suspend', 'users:unsuspend', 'users:unban',
    'brackets:unlock', 'matches:unlock',
    'venues:unpublish', 'venues:reject',
    'venue_stations:unlock',
    'registrations:reject',
    'members:unban',
    'wallets:unfreeze',
    'moderation:reject', 'moderation:dismiss',
    'verification:reject',
    'sponsors:reject_application',
    'tournaments:reject', 'tournaments:unfeature',
    'organizations:suspend'
  )
);

-- Step 3: Delete the dead permission rows themselves.
DELETE FROM admin_permissions
WHERE name IN (
  'users:suspend', 'users:unsuspend', 'users:unban',
  'brackets:unlock', 'matches:unlock',
  'venues:unpublish', 'venues:reject',
  'venue_stations:unlock',
  'registrations:reject',
  'members:unban',
  'wallets:unfreeze',
  'moderation:reject', 'moderation:dismiss',
  'verification:reject',
  'sponsors:reject_application',
  'tournaments:reject', 'tournaments:unfeature',
  'organizations:suspend'
);
