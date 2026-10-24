-- Backfill player_riot_accounts from the legacy riot_accounts table.
-- riot_accounts columns used: user_id, puuid, game_name, tag_line, region, linked_at
-- player_riot_accounts target columns: user_id, riot_puuid, game_name, tag_line, region, created_at
-- verified_at is left NULL — Riot API must re-confirm ownership for new rows.
-- The unique constraint on player_riot_accounts(user_id) means one account per user;
-- users already present in player_riot_accounts are skipped.

-- PRE-FLIGHT: Run on production before deploying, confirm expected count before proceeding:
-- SELECT COUNT(*) FROM public.riot_accounts ra
-- WHERE NOT EXISTS (SELECT 1 FROM public.player_riot_accounts pra WHERE pra.user_id = ra.user_id);
-- Expected: 0 after backfill runs; any positive count is the number of rows this migration will insert.

INSERT INTO public.player_riot_accounts (user_id, riot_puuid, game_name, tag_line, region, created_at)
SELECT
    ra.user_id,
    ra.puuid,
    ra.game_name,
    ra.tag_line,
    ra.region,
    COALESCE(ra.linked_at, now())
FROM public.riot_accounts ra
WHERE NOT EXISTS (
    SELECT 1
    FROM public.player_riot_accounts pra
    WHERE pra.user_id = ra.user_id
)
ON CONFLICT DO NOTHING;
