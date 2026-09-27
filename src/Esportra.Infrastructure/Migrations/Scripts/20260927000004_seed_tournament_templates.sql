-- Re-seed tournament_templates rows that were missed on first deploy.
-- The original 20260927000001 seed ran while the game catalog was not yet active,
-- so all INSERT...SELECT statements produced 0 rows. The catalog is now active;
-- this migration back-fills the 10 default templates. All statements are
-- ON CONFLICT (slug) DO NOTHING so it is safe to run on environments where
-- templates already exist.

INSERT INTO tournament_templates (game_catalog_id, slug, rules_text, rules_source_url, rules_updated_at, default_best_of, default_max_teams, recommended_team_counts, is_publisher_endorsed, is_active, sort_order)
SELECT
  g.id,
  'valorant',
  'Based on VCT (Valorant Champions Tour) official competitive rulebook. Matches are played in Bo3 or Bo5 format. Standard match is played to 13 rounds; overtime triggers at 12-12 and continues until one team wins by 2 rounds. Each team receives one tactical timeout per half (freezes buy phase for 60 seconds). Agent selection occurs per map before the match begins. Map veto follows a ban-ban-pick-pick-pick-pick-decider format for Bo3 (7-map pool). Side selection is determined by higher seed or coin flip. Economy rules follow standard competitive settings with full buy phases between rounds.',
  'https://valorantesports.com/rules',
  now(),
  3,
  16,
  ARRAY[8, 16, 32],
  false,
  true,
  1
FROM game_catalog_games g
INNER JOIN game_catalog_versions v ON g.version_id = v.id
WHERE g.slug = 'valorant' AND v.is_active = true AND v.status = 'active'
ON CONFLICT (slug) DO NOTHING;

INSERT INTO tournament_templates (game_catalog_id, slug, rules_text, rules_source_url, rules_updated_at, default_best_of, default_max_teams, recommended_team_counts, is_publisher_endorsed, is_active, sort_order)
SELECT
  g.id,
  'rainbow-six-siege',
  'Based on Rainbow Six Siege official competitive rules. Matches are played in Bo3 format with each map played to best-of-7 rounds in regulation. Operator ban phase allows 4 total bans (2 per team) before the round begins. Map veto process follows ban-ban-pick-pick-pick-pick-decider format from a 9-map pool. Overtime triggers at 3-3 in regulation and continues first-to-4 rounds, must win by 2. Each team receives one tactical timeout per map (30 seconds). Side selection alternates each round (attackers/defenders swap). Drone phase and preparation phase timers follow official competitive settings.',
  'https://www.ubisoft.com/en-us/esports/rainbow-six/siege/rules',
  now(),
  3,
  16,
  ARRAY[8, 16, 32],
  false,
  true,
  2
FROM game_catalog_games g
INNER JOIN game_catalog_versions v ON g.version_id = v.id
WHERE g.slug = 'rainbow-six-siege' AND v.is_active = true AND v.status = 'active'
ON CONFLICT (slug) DO NOTHING;

INSERT INTO tournament_templates (game_catalog_id, slug, rules_text, rules_source_url, rules_updated_at, default_best_of, default_max_teams, recommended_team_counts, is_publisher_endorsed, is_active, sort_order)
SELECT
  g.id,
  'lol',
  'Based on Riot Games official League of Legends tournament rules. Matches are typically played in Bo1 or Bo3 format depending on tournament stage. Champion select uses tournament draft mode with 5 bans per team. Pause protocol allows each team to request pauses for technical issues or emergencies (limited duration per match). Remake conditions apply before 3:00 in-game time if a player disconnects or a critical bug occurs. Game of record rules determine when a match result is official (typically after first blood or 2:00 elapsed). Chronobreak protocol may be invoked for game-breaking bugs that materially affect competitive integrity. Side selection (blue/red) is determined by higher seed or coin flip.',
  'https://lolesports.com/rules',
  now(),
  1,
  16,
  ARRAY[8, 16, 32],
  false,
  true,
  3
FROM game_catalog_games g
INNER JOIN game_catalog_versions v ON g.version_id = v.id
WHERE g.slug = 'lol' AND v.is_active = true AND v.status = 'active'
ON CONFLICT (slug) DO NOTHING;

INSERT INTO tournament_templates (game_catalog_id, slug, rules_text, rules_source_url, rules_updated_at, default_best_of, default_max_teams, recommended_team_counts, is_publisher_endorsed, is_active, sort_order)
SELECT
  g.id,
  'dota2',
  'Based on Valve official Dota Pro Circuit rules. Matches are played in Bo3 or Bo5 format. Draft uses Captain''s Mode with structured ban phases: first ban phase allows 2 bans per team, followed by hero picks, then second ban phase with 3 bans per team (6 total bans per team across both phases). Side selection (Radiant/Dire) alternates in series or is determined by higher seed for game 1. Pause rules allow each team up to 5 minutes of pause time per match, with a 10-minute total pause limit. Remake is permitted before first blood if a critical bug affects competitive integrity. Coach communication is allowed during the draft phase only; no communication during live gameplay.',
  'https://www.dota2.com/esports/rules',
  now(),
  3,
  16,
  ARRAY[8, 16, 32],
  false,
  true,
  4
FROM game_catalog_games g
INNER JOIN game_catalog_versions v ON g.version_id = v.id
WHERE g.slug = 'dota2' AND v.is_active = true AND v.status = 'active'
ON CONFLICT (slug) DO NOTHING;

INSERT INTO tournament_templates (game_catalog_id, slug, rules_text, rules_source_url, rules_updated_at, default_best_of, default_max_teams, recommended_team_counts, is_publisher_endorsed, is_active, sort_order)
SELECT
  g.id,
  'rocket-league',
  'Based on RLCS (Rocket League Championship Series) official competition rules. Matches are played in Bo5 or Bo7 format depending on tournament stage. Game settings use standard competitive arena with no mutators enabled. Ball type, boost settings, and physics follow default competitive configuration. Overtime uses golden goal rules (first goal wins). Series format does not include overtime in regulation games; tied games proceed to overtime until a winner is determined. Replay review is available to officials for disputed calls. One substitution is allowed per series break (between maps) if a rostered substitute is available. Teams have a 5-minute ready period before each series begins. Standard match length is 5 minutes of regulation time.',
  'https://www.rocketleagueesports.com/rules',
  now(),
  5,
  16,
  ARRAY[8, 16, 32],
  false,
  true,
  5
FROM game_catalog_games g
INNER JOIN game_catalog_versions v ON g.version_id = v.id
WHERE g.slug = 'rocket-league' AND v.is_active = true AND v.status = 'active'
ON CONFLICT (slug) DO NOTHING;

INSERT INTO tournament_templates (game_catalog_id, slug, rules_text, rules_source_url, rules_updated_at, default_best_of, default_max_teams, recommended_team_counts, is_publisher_endorsed, is_active, sort_order)
SELECT
  g.id,
  'tekken8',
  'Based on Tekken World Tour official rules. Matches are played in Bo3 or Bo5 format (first to 2 wins or first to 3 wins). Character selection for the first game uses double blind pick (both players select without seeing opponent''s choice). After game 1, the winner is locked to their character and the loser may switch characters. Stage selection is determined by the loser after game 1; first game uses a neutral stage or random select. Button check is allowed before the match begins to verify controller functionality. Timeout rules follow standard Tekken mechanics; no additional time is granted. No coaching is allowed during an active set (between games). Players have 60 seconds between games to prepare for the next match.',
  'https://tekkenworldtour.com/rules',
  now(),
  3,
  16,
  ARRAY[8, 16, 32],
  false,
  true,
  6
FROM game_catalog_games g
INNER JOIN game_catalog_versions v ON g.version_id = v.id
WHERE g.slug = 'tekken8' AND v.is_active = true AND v.status = 'active'
ON CONFLICT (slug) DO NOTHING;

INSERT INTO tournament_templates (game_catalog_id, slug, rules_text, rules_source_url, rules_updated_at, default_best_of, default_max_teams, recommended_team_counts, is_publisher_endorsed, is_active, sort_order)
SELECT
  g.id,
  'eafc',
  'Based on EA Sports FC official esports rules. Matches are played in Bo1 or Bo3 format depending on tournament stage. Game settings use 6-minute halves on professional difficulty with standard competitive sliders. Team selection occurs before the match on the team selection screen; no pauses are allowed during this phase. Disconnection protocol: if a disconnect occurs before 15 in-game minutes (combined halves), the match is restarted from kickoff; disconnects after 15 minutes result in a forfeit for the disconnecting player. If regulation time ends in a tie, extra time (2x 3-minute halves) is played followed by penalties if still tied. Controller settings and button mappings may be adjusted before the match begins. Substitutions and tactical adjustments follow in-game mechanics only.',
  'https://www.ea.com/games/ea-sports-fc/compete/rules',
  now(),
  1,
  16,
  ARRAY[8, 16, 32],
  false,
  true,
  7
FROM game_catalog_games g
INNER JOIN game_catalog_versions v ON g.version_id = v.id
WHERE g.slug = 'eafc' AND v.is_active = true AND v.status = 'active'
ON CONFLICT (slug) DO NOTHING;

INSERT INTO tournament_templates (game_catalog_id, slug, rules_text, rules_source_url, rules_updated_at, default_best_of, default_max_teams, recommended_team_counts, is_publisher_endorsed, is_active, sort_order)
SELECT
  g.id,
  'fortnite',
  'Based on Fortnite Champion Series official rules. Tournaments use a multi-game match point format where teams compete across multiple matches. Scoring combines placement points and elimination points: higher placements award more points (1st = 25pts down to 21st+ = 1pt) and each elimination awards 1 point. Storm surge mechanics are enabled to reduce server load in crowded late-game circles. Prohibited items list is enforced by tournament officials (certain weapons/items may be disabled for competitive balance). Anti-cheat software (BattlEye) is required and must remain active. Broadcast delay is enforced for competitive integrity (typically 2+ minutes) to prevent stream sniping. Match officials monitor for teaming, collusion, and other rule violations. Final standings are determined by total points across all games played.',
  'https://www.epicgames.com/fortnite/competitive/rules',
  now(),
  1,
  20,
  ARRAY[10, 20, 40],
  false,
  true,
  8
FROM game_catalog_games g
INNER JOIN game_catalog_versions v ON g.version_id = v.id
WHERE g.slug = 'fortnite' AND v.is_active = true AND v.status = 'active'
ON CONFLICT (slug) DO NOTHING;

INSERT INTO tournament_templates (game_catalog_id, slug, rules_text, rules_source_url, rules_updated_at, default_best_of, default_max_teams, recommended_team_counts, is_publisher_endorsed, is_active, sort_order)
SELECT
  g.id,
  'apex',
  'Based on Apex Legends Global Series official rules. Tournaments use a match point format where teams compete across multiple matches to reach a point threshold. Scoring combines placement points and kill points: top placements award points (1st = 12pts, 2nd = 9pts, scaling down to 15th+ = 1pt) and each kill awards 1 point up to a maximum of 6 kills per team per match. Ring settings and zone timers follow standardized competitive configuration. Legend restrictions may be applied if exploits are discovered (announced before tournament). Disconnection protocol: if a player disconnects, the team may continue 2v3; no remake unless the disconnect occurs before landing. Observer and replay systems are used for match verification and dispute resolution. Anti-cheat is required and monitored throughout the event.',
  'https://www.ea.com/games/apex-legends/compete/rules',
  now(),
  1,
  20,
  ARRAY[10, 20, 40],
  false,
  true,
  9
FROM game_catalog_games g
INNER JOIN game_catalog_versions v ON g.version_id = v.id
WHERE g.slug = 'apex' AND v.is_active = true AND v.status = 'active'
ON CONFLICT (slug) DO NOTHING;

INSERT INTO tournament_templates (game_catalog_id, slug, rules_text, rules_source_url, rules_updated_at, default_best_of, default_max_teams, recommended_team_counts, is_publisher_endorsed, is_active, sort_order)
SELECT
  g.id,
  'pubg',
  'Based on PUBG Global Series official rules. Tournaments use a match point format where teams compete across multiple matches. Scoring combines placement points and kill points: top placements award points (1st = 10pts, 2nd = 6pts, 3rd = 5pts, scaling down to 8th+ = 1pt) and each kill awards 1 point with no cap. Circle settings and zone timers follow standardized competitive configuration to ensure fair play. Vehicle rules prohibit teaming between squads; vehicles may only be used by the squad that controls them. Prohibited item or area restrictions may be enforced by officials. Disconnection protocol: if a player disconnects, the squad may continue with remaining members; no remake unless server-wide issue occurs. Replay review system is available for disputes and rule violations. Anti-cheat is required and monitored throughout the event. Final standings are determined by total points across all matches played.',
  'https://pubgesports.com/rules',
  now(),
  1,
  20,
  ARRAY[10, 20, 40],
  false,
  true,
  10
FROM game_catalog_games g
INNER JOIN game_catalog_versions v ON g.version_id = v.id
WHERE g.slug = 'pubg' AND v.is_active = true AND v.status = 'active'
ON CONFLICT (slug) DO NOTHING;
