-- Read-only operator diagnostic. Replace the zero UUID with the affected listing ID.
-- Requires existing read access to private stats tables; does not change RLS or data.
-- Output intentionally excludes user IDs, Riot IDs, match IDs and profile details.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '5s';

WITH target AS (
    SELECT pgs.*, l.user_id AS listing_owner_id, ra.puuid AS linked_account,
           anchor.started_at AS cursor_time
    FROM public.lfg_listings l
    LEFT JOIN public.riot_accounts ra ON ra.user_id = l.user_id
    LEFT JOIN public.player_game_stats pgs ON pgs.user_id = l.user_id AND pgs.game_slug = 'valorant'
    LEFT JOIN public.valorant_player_matches anchor ON anchor.user_id = l.user_id
        AND anchor.provider_player_id = ra.puuid AND anchor.match_id = pgs.last_match_id
        AND anchor.act_id = pgs.last_act_id
    WHERE l.id = '00000000-0000-0000-0000-000000000000'::uuid AND l.game_slug = 'valorant'
)
SELECT s.linked_account IS NOT NULL AS has_linked_account,
       s.user_id IS NOT NULL AS has_stats,
       s.provider_player_id = s.linked_account AS stats_belong_to_linked_account,
       s.sync_status, s.synced_at, s.aggregation_version,
       s.matches_analyzed, s.headshot_data_complete, s.hs_pct,
       s.cursor_time IS NOT NULL AS has_processed_cursor,
       a.archived_matches, a.complete_hit_matches, a.invalid_hit_matches,
       COALESCE(a.archived_matches = s.matches_analyzed
           AND a.wins = s.win_count AND a.losses = s.loss_count
           AND a.kills = s.total_kills AND a.deaths = s.total_deaths
           AND a.assists = s.total_assists AND a.score = s.total_score, false) AS archive_counters_match,
       a.shots AS archived_landed_hits,
       b.account_backlog_matches, b.account_backlog_due, b.next_retry_at
FROM target s
LEFT JOIN LATERAL (
    SELECT count(*) AS archived_matches,
           count(*) FILTER (WHERE m.headshot_data_complete IS TRUE) AS complete_hit_matches,
           count(*) FILTER (WHERE m.headshots < 0 OR m.shots < m.headshots) AS invalid_hit_matches,
           count(*) FILTER (WHERE m.won IS TRUE) AS wins,
           count(*) FILTER (WHERE m.won IS FALSE) AS losses,
           sum(m.kills) AS kills, sum(m.deaths) AS deaths, sum(m.assists) AS assists,
           sum(m.score) AS score, sum(m.shots) AS shots
    FROM public.valorant_player_matches m
    WHERE m.user_id = s.listing_owner_id AND m.provider_player_id = s.linked_account
      AND m.act_id = s.last_act_id AND m.queue_id = 'competitive' AND m.match_data_version >= 1
      AND m.started_at <= s.cursor_time
) a ON true
LEFT JOIN LATERAL (
    -- Account-wide: backlog rows may not have an act until their details are fetched.
    SELECT count(*) AS account_backlog_matches,
           count(*) FILTER (WHERE retry_after IS NULL OR retry_after <= now()) AS account_backlog_due,
           min(retry_after) FILTER (WHERE retry_after > now()) AS next_retry_at
    FROM public.valorant_match_backlog b
    WHERE b.user_id = s.listing_owner_id AND b.provider_player_id = s.linked_account
) b ON true;

ROLLBACK;
