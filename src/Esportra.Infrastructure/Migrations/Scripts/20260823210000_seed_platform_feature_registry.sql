-- Seed the curated platform feature registry (Admin Centre > Feature Flags catalog).
-- Idempotent: existing rows (and any admin state) are never overwritten.

INSERT INTO feature_flags (key, name, description, flag_type, default_value, is_enabled)
VALUES
    ('battle-royale', 'Battle Royale Mode', 'BR lobbies, join flow, evidence and results. Off hides BR surfaces and blocks /api/br routes. Existing BR data is preserved.', 'boolean', 'true'::jsonb, TRUE),
    ('tournament-registration', 'Tournament Registration', 'New registrations for tournaments. Off hides register buttons and blocks the registration endpoint. Existing participants are unaffected.', 'boolean', 'true'::jsonb, TRUE),
    ('team-invites', 'Team Invites', 'Creating and accepting team invites. Off blocks invite creation/redemption; teams, rosters and members stay fully manageable.', 'boolean', 'true'::jsonb, TRUE),
    ('wallet-pos', 'Wallet & POS', 'Venue wallets, top-ups and POS orders. Off hides venue commerce surfaces and blocks wallet/POS endpoints. Balances and orders are preserved.', 'boolean', 'true'::jsonb, TRUE),
    ('sponsor-ads', 'Sponsor Ads & Ticker', 'Homepage ticker, partner showcase placements and impression/click beacons. Off stops rendering and tracking; sponsor records and stats stay intact.', 'boolean', 'true'::jsonb, TRUE),
    ('broadcasts', 'Broadcasts & Notifications', 'User broadcast inbox and broadcast sending. Off pauses delivery and hides the inbox; drafts, templates and history are preserved.', 'boolean', 'true'::jsonb, TRUE),
    ('ghost-mode', 'Ghost Mode', 'Admin impersonation sessions. Off blocks starting new ghost sessions and approval requests. Audit trails remain queryable while disabled.', 'boolean', 'true'::jsonb, TRUE)
ON CONFLICT (key) DO NOTHING;
