-- Admin RBAC v2 permission catalog.
-- Adds metadata for the admin UI and upserts the expanded granular permission set.

ALTER TABLE public.admin_permissions
  ADD COLUMN IF NOT EXISTS label TEXT,
  ADD COLUMN IF NOT EXISTS category TEXT,
  ADD COLUMN IF NOT EXISTS risk_level TEXT NOT NULL DEFAULT 'normal',
  ADD COLUMN IF NOT EXISTS sort_order INTEGER NOT NULL DEFAULT 0,
  ADD COLUMN IF NOT EXISTS is_system BOOLEAN NOT NULL DEFAULT FALSE,
  ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT now();

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'admin_permissions_risk_level_check'
  ) THEN
    ALTER TABLE public.admin_permissions
      ADD CONSTRAINT admin_permissions_risk_level_check
      CHECK (risk_level IN ('normal', 'sensitive', 'destructive', 'god_mode'));
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_admin_permissions_resource_action
  ON public.admin_permissions (resource, action);

CREATE INDEX IF NOT EXISTS idx_admin_permissions_category_sort
  ON public.admin_permissions (category, sort_order, name);

WITH permission_seed(name, sort_order) AS (
  VALUES
    ('users:view', 100), ('users:create', 101), ('users:edit', 102), ('users:suspend', 103),
    ('users:unsuspend', 104), ('users:ban', 105), ('users:unban', 106), ('users:delete', 107),
    ('users:export', 108), ('users:impersonate', 109), ('users:audit', 110),

    ('profiles:view', 200), ('profiles:edit', 201), ('profiles:delete', 202),
    ('profiles:restore', 203), ('profiles:audit', 204),

    ('admin_users:view', 300), ('admin_users:assign_role', 301),
    ('admin_users:revoke_role', 302), ('admin_users:audit', 303),

    ('rbac:view', 400), ('rbac:create_role', 401), ('rbac:edit_role', 402),
    ('rbac:delete_role', 403), ('rbac:assign_permissions', 404),
    ('rbac:preview_effective_permissions', 405), ('rbac:audit', 406),

    ('data_admin:view', 500), ('data_admin:create', 501), ('data_admin:edit', 502),
    ('data_admin:delete', 503), ('data_admin:restore', 504), ('data_admin:export', 505),
    ('data_admin:override', 506), ('data_admin:audit', 507),

    ('impersonation:start', 600), ('impersonation:stop', 601),
    ('impersonation:view_sessions', 602), ('impersonation:audit', 603),

    ('feature_flags:view', 700), ('feature_flags:create', 701), ('feature_flags:edit', 702),
    ('feature_flags:delete', 703), ('feature_flags:toggle', 704), ('feature_flags:audit', 705),

    ('broadcasts:view', 710), ('broadcasts:create', 711), ('broadcasts:edit', 712),
    ('broadcasts:delete', 713), ('broadcasts:send', 714),

    ('ghost:audit', 720), ('users:impersonate:full', 721), ('users:impersonate:approve', 722),

    ('tournaments:view', 800), ('tournaments:create', 801), ('tournaments:edit', 802),
    ('tournaments:approve', 803), ('tournaments:reject', 804), ('tournaments:feature', 805),
    ('tournaments:unfeature', 806), ('tournaments:cancel', 807), ('tournaments:delete', 808),
    ('tournaments:restore', 809), ('tournaments:export', 810), ('tournaments:override', 811),
    ('tournaments:audit', 812),

    ('brackets:view', 900), ('brackets:edit', 901), ('brackets:regenerate', 902),
    ('brackets:reset', 903), ('brackets:lock', 904), ('brackets:unlock', 905),
    ('brackets:override', 906), ('brackets:audit', 907),

    ('matches:view', 1000), ('matches:edit', 1001), ('matches:schedule', 1002),
    ('matches:report_result', 1003), ('matches:override_result', 1004),
    ('matches:lock', 1005), ('matches:unlock', 1006), ('matches:audit', 1007),

    ('registrations:view', 1100), ('registrations:edit', 1101), ('registrations:approve', 1102),
    ('registrations:reject', 1103), ('registrations:cancel', 1104), ('registrations:refund', 1105),
    ('registrations:export', 1106), ('registrations:audit', 1107),

    ('invitations:view', 1200), ('invitations:create', 1201), ('invitations:revoke', 1202),
    ('invitations:resend', 1203), ('invitations:export', 1204), ('invitations:audit', 1205),

    ('disputes:view', 1300), ('disputes:comment', 1301), ('disputes:resolve', 1302),
    ('disputes:reject', 1303), ('disputes:escalate', 1304), ('disputes:lift_ban', 1305),
    ('disputes:delete', 1306), ('disputes:export', 1307), ('disputes:audit', 1308),

    ('venues:view', 1400), ('venues:create', 1401), ('venues:edit', 1402),
    ('venues:approve', 1403), ('venues:reject', 1404), ('venues:publish', 1405),
    ('venues:unpublish', 1406), ('venues:delete', 1407), ('venues:restore', 1408),
    ('venues:export', 1409), ('venues:audit', 1410),

    ('venue_stations:view', 1500), ('venue_stations:create', 1501),
    ('venue_stations:edit', 1502), ('venue_stations:lock', 1503),
    ('venue_stations:unlock', 1504), ('venue_stations:delete', 1505),
    ('venue_stations:audit', 1506),

    ('venue_sessions:view', 1600), ('venue_sessions:create', 1601),
    ('venue_sessions:edit', 1602), ('venue_sessions:end', 1603),
    ('venue_sessions:refund', 1604), ('venue_sessions:export', 1605),
    ('venue_sessions:audit', 1606),

    ('venue_staff:view', 1700), ('venue_staff:invite', 1701), ('venue_staff:edit', 1702),
    ('venue_staff:revoke', 1703), ('venue_staff:audit', 1704),

    ('members:view', 1800), ('members:create', 1801), ('members:edit', 1802),
    ('members:ban', 1803), ('members:unban', 1804), ('members:export', 1805),
    ('members:audit', 1806),

    ('bookings:view', 1900), ('bookings:create', 1901), ('bookings:edit', 1902),
    ('bookings:cancel', 1903), ('bookings:refund', 1904), ('bookings:export', 1905),
    ('bookings:audit', 1906),

    ('teams:view', 2000), ('teams:create', 2001), ('teams:edit', 2002),
    ('teams:disband', 2003), ('teams:restore', 2004), ('teams:transfer_captain', 2005),
    ('teams:remove_member', 2006), ('teams:export', 2007), ('teams:audit', 2008),

    ('organizations:view', 2100), ('organizations:create', 2101), ('organizations:edit', 2102),
    ('organizations:suspend', 2103), ('organizations:restore', 2104), ('organizations:audit', 2105),

    ('sponsors:view', 2200), ('sponsors:create', 2201), ('sponsors:edit', 2202),
    ('sponsors:approve_application', 2203), ('sponsors:reject_application', 2204),
    ('sponsors:delete', 2205), ('sponsors:export', 2206), ('sponsors:audit', 2207),

    ('verification:view', 2300), ('verification:approve', 2301), ('verification:reject', 2302),
    ('verification:delete', 2303), ('verification:export', 2304), ('verification:audit', 2305),

    ('licenses:view', 2400), ('licenses:create', 2401), ('licenses:revoke', 2402),
    ('licenses:reinstate', 2403), ('licenses:delete', 2404), ('licenses:export', 2405),
    ('licenses:audit', 2406),

    ('pos:view', 2500), ('pos:create_order', 2501), ('pos:edit_order', 2502),
    ('pos:refund', 2503), ('pos:void', 2504), ('pos:export', 2505), ('pos:audit', 2506),

    ('wallets:view', 2600), ('wallets:adjust', 2601), ('wallets:freeze', 2602),
    ('wallets:unfreeze', 2603), ('wallets:audit', 2604),

    ('loyalty:view', 2700), ('loyalty:adjust', 2701), ('loyalty:reset', 2702),
    ('loyalty:audit', 2703),

    ('payments:view', 2800), ('payments:refund', 2801), ('payments:reconcile', 2802),
    ('payments:export', 2803), ('payments:audit', 2804),

    ('analytics:view', 2900), ('analytics:export', 2901), ('dashboard:view', 2902),

    ('system:settings', 3000), ('system:audit', 3001), ('system:billing', 3002),
    ('system:config_view', 3003), ('system:config_edit', 3004), ('system:kill-switch', 3005),
    ('audit:view', 3100), ('audit:export', 3101),
    ('settings:view', 3200), ('settings:edit', 3201), ('settings:audit', 3202),

    ('security:view', 3300), ('security:manage_ip_allowlist', 3301),
    ('security:revoke_sessions', 3302), ('security:view_sessions', 3303),
    ('security:audit', 3304),

    ('gdpr:view', 3400), ('gdpr:process', 3401), ('gdpr:reject', 3402),
    ('gdpr:export', 3403), ('gdpr:audit', 3404),

    ('reports:view', 3500), ('reports:create', 3501), ('reports:edit', 3502),
    ('reports:delete', 3503), ('reports:run', 3504), ('reports:export', 3505),
    ('reports:audit', 3506),

    ('alerts:view', 3600), ('alerts:acknowledge', 3601), ('alerts:resolve', 3602),
    ('alerts:bulk_acknowledge', 3603), ('alerts:audit', 3604),

    ('content:moderate', 3700), ('content:create', 3701), ('content:delete', 3702),
    ('moderation:view', 3800), ('moderation:approve', 3801), ('moderation:reject', 3802),
    ('moderation:dismiss', 3803), ('moderation:audit', 3804),

    ('notifications:view', 3900), ('notifications:create', 3901),
    ('notifications:broadcast', 3902), ('notifications:delete', 3903),
    ('notifications:audit', 3904),

    ('games:view', 4000), ('games:create', 4001), ('games:edit', 4002),
    ('games:delete', 4003), ('games:publish', 4004), ('games:reset_draft', 4005),
    ('games:upload_assets', 4006), ('games:audit', 4007), ('games:manage', 4008)
),
normalized AS (
  SELECT
    name,
    split_part(name, ':', 1) AS resource,
    split_part(name, ':', 2) AS action,
    initcap(replace(replace(split_part(name, ':', 2), '_', ' '), '-', ' ')) AS label,
    initcap(replace(split_part(name, ':', 1), '_', ' ')) AS category,
    CASE
      WHEN name LIKE 'data_admin:%' OR name LIKE 'impersonation:%' OR name = 'system:kill-switch'
        THEN 'god_mode'
      WHEN split_part(name, ':', 2) IN (
        'delete', 'ban', 'disband', 'refund', 'void', 'override',
        'override_result', 'assign_permissions', 'delete_role', 'impersonate'
      )
        THEN 'destructive'
      WHEN split_part(name, ':', 2) IN (
        'suspend', 'revoke', 'revoke_role', 'assign_role', 'approve',
        'reject', 'toggle', 'manage_ip_allowlist', 'revoke_sessions',
        'adjust', 'freeze', 'unfreeze', 'publish', 'unpublish', 'cancel'
      )
        THEN 'sensitive'
      ELSE 'normal'
    END AS risk_level,
    sort_order
  FROM permission_seed
)
INSERT INTO public.admin_permissions
  (name, description, resource, action, label, category, risk_level, sort_order, is_system, updated_at)
SELECT
  name,
  label || ' permission for ' || category,
  resource,
  action,
  label,
  category,
  risk_level,
  sort_order,
  TRUE,
  now()
FROM normalized
ON CONFLICT (name) DO UPDATE SET
  description = EXCLUDED.description,
  resource = EXCLUDED.resource,
  action = EXCLUDED.action,
  label = EXCLUDED.label,
  category = EXCLUDED.category,
  risk_level = EXCLUDED.risk_level,
  sort_order = EXCLUDED.sort_order,
  is_system = TRUE,
  updated_at = now();

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'admin_role_permissions_role_id_permission_id_key'
  ) THEN
    ALTER TABLE public.admin_role_permissions
      ADD CONSTRAINT admin_role_permissions_role_id_permission_id_key
      UNIQUE (role_id, permission_id);
  END IF;
END $$;

DROP TABLE IF EXISTS pg_temp.admin_role_permission_seed;
CREATE TEMP TABLE admin_role_permission_seed (
  role_key TEXT NOT NULL,
  permission_name TEXT NOT NULL
) ON COMMIT DROP;

INSERT INTO admin_role_permission_seed (role_key, permission_name)
VALUES
    -- ops_admin
    ('ops_admin','users:view'), ('ops_admin','users:edit'), ('ops_admin','users:suspend'),
    ('ops_admin','users:unsuspend'), ('ops_admin','profiles:view'), ('ops_admin','profiles:edit'),
    ('ops_admin','tournaments:view'), ('ops_admin','tournaments:create'), ('ops_admin','tournaments:edit'),
    ('ops_admin','tournaments:approve'), ('ops_admin','tournaments:reject'), ('ops_admin','tournaments:feature'),
    ('ops_admin','tournaments:unfeature'), ('ops_admin','tournaments:cancel'), ('ops_admin','tournaments:export'),
    ('ops_admin','brackets:view'), ('ops_admin','brackets:edit'), ('ops_admin','brackets:lock'),
    ('ops_admin','brackets:unlock'), ('ops_admin','matches:view'), ('ops_admin','matches:edit'),
    ('ops_admin','matches:schedule'), ('ops_admin','registrations:view'), ('ops_admin','registrations:edit'),
    ('ops_admin','registrations:approve'), ('ops_admin','registrations:reject'), ('ops_admin','registrations:export'),
    ('ops_admin','invitations:view'), ('ops_admin','invitations:create'), ('ops_admin','invitations:revoke'),
    ('ops_admin','teams:view'), ('ops_admin','teams:edit'), ('ops_admin','teams:disband'),
    ('ops_admin','teams:transfer_captain'), ('ops_admin','teams:remove_member'), ('ops_admin','teams:export'),
    ('ops_admin','organizations:view'), ('ops_admin','organizations:edit'),
    ('ops_admin','disputes:view'), ('ops_admin','disputes:comment'), ('ops_admin','disputes:resolve'),
    ('ops_admin','disputes:reject'), ('ops_admin','disputes:escalate'), ('ops_admin','disputes:export'),
    ('ops_admin','venues:view'), ('ops_admin','venues:edit'), ('ops_admin','venues:approve'),
    ('ops_admin','venues:reject'), ('ops_admin','venues:publish'), ('ops_admin','venues:unpublish'),
    ('ops_admin','venues:export'), ('ops_admin','venue_stations:view'), ('ops_admin','venue_stations:create'),
    ('ops_admin','venue_stations:edit'), ('ops_admin','venue_stations:lock'), ('ops_admin','venue_stations:unlock'),
    ('ops_admin','venue_sessions:view'), ('ops_admin','venue_sessions:edit'), ('ops_admin','venue_sessions:end'),
    ('ops_admin','venue_sessions:export'), ('ops_admin','venue_staff:view'), ('ops_admin','venue_staff:invite'),
    ('ops_admin','venue_staff:edit'), ('ops_admin','venue_staff:revoke'), ('ops_admin','members:view'),
    ('ops_admin','members:edit'), ('ops_admin','members:ban'), ('ops_admin','members:unban'),
    ('ops_admin','members:export'), ('ops_admin','bookings:view'), ('ops_admin','bookings:edit'),
    ('ops_admin','bookings:cancel'), ('ops_admin','bookings:export'), ('ops_admin','verification:view'),
    ('ops_admin','verification:approve'), ('ops_admin','verification:reject'), ('ops_admin','verification:export'),
    ('ops_admin','moderation:view'), ('ops_admin','moderation:approve'), ('ops_admin','moderation:reject'),
    ('ops_admin','moderation:dismiss'), ('ops_admin','content:moderate'), ('ops_admin','content:delete'),
    ('ops_admin','analytics:view'), ('ops_admin','analytics:export'), ('ops_admin','dashboard:view'), ('ops_admin','alerts:view'),
    ('ops_admin','alerts:acknowledge'), ('ops_admin','alerts:resolve'), ('ops_admin','alerts:bulk_acknowledge'),
    ('ops_admin','reports:view'), ('ops_admin','reports:create'), ('ops_admin','reports:edit'),
    ('ops_admin','reports:run'), ('ops_admin','reports:export'), ('ops_admin','games:view'),
    ('ops_admin','games:edit'), ('ops_admin','games:publish'), ('ops_admin','games:reset_draft'),
    ('ops_admin','games:upload_assets'), ('ops_admin','games:manage'), ('ops_admin','system:audit'),
    ('ops_admin','audit:view'), ('ops_admin','settings:view'),

    -- moderator
    ('moderator','users:view'), ('moderator','users:suspend'), ('moderator','users:unsuspend'),
    ('moderator','users:ban'), ('moderator','users:unban'), ('moderator','profiles:view'),
    ('moderator','tournaments:view'), ('moderator','teams:view'), ('moderator','disputes:view'),
    ('moderator','disputes:comment'), ('moderator','disputes:resolve'), ('moderator','disputes:reject'),
    ('moderator','disputes:escalate'), ('moderator','disputes:lift_ban'), ('moderator','moderation:view'),
    ('moderator','moderation:approve'), ('moderator','moderation:reject'), ('moderator','moderation:dismiss'),
    ('moderator','content:moderate'), ('moderator','content:delete'), ('moderator','verification:view'),
    ('moderator','verification:reject'), ('moderator','dashboard:view'), ('moderator','alerts:view'), ('moderator','alerts:acknowledge'),

    -- finance_admin
    ('finance_admin','analytics:view'), ('finance_admin','analytics:export'), ('finance_admin','dashboard:view'), ('finance_admin','system:billing'),
    ('finance_admin','payments:view'), ('finance_admin','payments:refund'), ('finance_admin','payments:reconcile'),
    ('finance_admin','payments:export'), ('finance_admin','wallets:view'), ('finance_admin','wallets:adjust'),
    ('finance_admin','loyalty:view'), ('finance_admin','loyalty:adjust'), ('finance_admin','pos:view'),
    ('finance_admin','pos:refund'), ('finance_admin','pos:export'), ('finance_admin','sponsors:view'),
    ('finance_admin','sponsors:create'), ('finance_admin','sponsors:edit'),
    ('finance_admin','sponsors:approve_application'), ('finance_admin','sponsors:reject_application'),
    ('finance_admin','sponsors:export'), ('finance_admin','licenses:view'), ('finance_admin','licenses:create'),
    ('finance_admin','licenses:revoke'), ('finance_admin','licenses:reinstate'), ('finance_admin','licenses:export'),
    ('finance_admin','reports:view'), ('finance_admin','reports:create'), ('finance_admin','reports:edit'),
    ('finance_admin','reports:run'), ('finance_admin','reports:export'), ('finance_admin','system:audit'),
    ('finance_admin','audit:view'),

    -- support_admin
    ('support_admin','users:view'), ('support_admin','profiles:view'), ('support_admin','disputes:view'),
    ('support_admin','disputes:comment'), ('support_admin','disputes:escalate'), ('support_admin','tournaments:view'),
    ('support_admin','brackets:view'), ('support_admin','matches:view'), ('support_admin','registrations:view'),
    ('support_admin','invitations:view'), ('support_admin','venues:view'), ('support_admin','venue_stations:view'),
    ('support_admin','venue_sessions:view'), ('support_admin','bookings:view'), ('support_admin','members:view'),
    ('support_admin','teams:view'), ('support_admin','organizations:view'), ('support_admin','verification:view'),
    ('support_admin','licenses:view'), ('support_admin','security:view'), ('support_admin','security:view_sessions'),
    ('support_admin','alerts:view'), ('support_admin','alerts:acknowledge');

-- Make built-in non-super roles authoritative. Custom roles are untouched.
DELETE FROM public.admin_role_permissions arp
USING public.admin_roles ar, public.admin_permissions ap
WHERE arp.role_id = ar.id
  AND arp.permission_id = ap.id
  AND ar.key IN ('ops_admin', 'moderator', 'finance_admin', 'support_admin')
  AND NOT EXISTS (
    SELECT 1
    FROM admin_role_permission_seed seed
    WHERE seed.role_key = ar.key
      AND seed.permission_name = ap.name
  );

INSERT INTO public.admin_role_permissions (role_id, permission_id)
SELECT ar.id, ap.id
FROM admin_role_permission_seed rs
JOIN public.admin_roles ar ON ar.key = rs.role_key
JOIN public.admin_permissions ap ON ap.name = rs.permission_name
ON CONFLICT DO NOTHING;

INSERT INTO public.admin_role_permissions (role_id, permission_id)
SELECT ar.id, ap.id
FROM public.admin_roles ar
CROSS JOIN public.admin_permissions ap
WHERE ar.key = 'super_admin'
ON CONFLICT DO NOTHING;
