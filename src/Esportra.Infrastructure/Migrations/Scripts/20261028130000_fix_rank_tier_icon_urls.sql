-- 20261028130000_fix_rank_tier_icon_urls.sql
-- Fix: rank tier icon URLs seeded with stale episode UUID (564d8e28…) that 404s.
-- Update to current episode UUID (03621f52-342b-cf4e-4f86-9350a49c6d04).

UPDATE public.game_catalog_rank_tiers
SET    icon_url = format(
           'https://media.valorant-api.com/competitivetiers/03621f52-342b-cf4e-4f86-9350a49c6d04/%s/largeicon.png',
           sort_order + 3
       )
WHERE  game_slug = 'valorant';
