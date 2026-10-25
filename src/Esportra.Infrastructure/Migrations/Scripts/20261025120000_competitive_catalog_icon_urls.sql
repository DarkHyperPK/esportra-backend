-- 20261025120000_competitive_catalog_icon_urls.sql
-- PROJ-075 T2: Add icon_url columns to the competitive catalog tables and seed
-- Valorant rank tier icons from the valorant-api.com CDN.
--
-- Two columns added (one concern: asset URL storage on catalog tables):
--   game_catalog_rank_tiers.icon_url  TEXT  nullable
--   game_catalog_game_roles.icon_url  TEXT  nullable
--
-- Valorant rank tier URLs are seeded deterministically (see mapping note below).
-- Valorant game role URLs are left NULL — see the note at the bottom.

-- ── ADD COLUMNS ───────────────────────────────────────────────────────────────

ALTER TABLE public.game_catalog_rank_tiers
    ADD COLUMN IF NOT EXISTS icon_url TEXT;

ALTER TABLE public.game_catalog_game_roles
    ADD COLUMN IF NOT EXISTS icon_url TEXT;

-- ── SEED: Valorant rank tier icon URLs ────────────────────────────────────────
--
-- CDN pattern (confirmed in CTO analysis PROJ-075 and codebase):
--   https://media.valorant-api.com/competitivetiers/{episode_uuid}/{tier}/largeicon.png
--
-- Episode UUID: 564d8e28-c226-3b10-8e0e-9cfc911a9ae4
-- Tier number mapping: valorant-api.com competitive tier index = sort_order + 3
--   Iron 1 (sort_order 0)  → tier 3
--   Iron 2 (sort_order 1)  → tier 4
--   Iron 3 (sort_order 2)  → tier 5
--   Bronze 1-3             → tiers 6-8
--   Silver 1-3             → tiers 9-11
--   Gold 1-3               → tiers 12-14
--   Platinum 1-3           → tiers 15-17
--   Diamond 1-3            → tiers 18-20
--   Ascendant 1-3          → tiers 21-23
--   Immortal 1-3           → tiers 24-26
--   Radiant (sort_order 24)→ tier 27
--
-- STABILITY NOTE: This UUID is a specific Valorant episode from valorant-api.com.
-- valorant-api.com releases new competitive tier episodes on each Valorant Act;
-- old episode UUIDs remain permanently accessible at their CDN paths, so existing
-- URLs do not break when a new act ships.  The frontend (useValorantCatalog.ts)
-- always fetches the LATEST episode for live use — the DB copy is the persistent
-- source of truth for catalog reads, so the episode UUID is intentionally frozen
-- here.  When Riot releases a new rank structure (e.g., adding or removing a
-- tier), run a follow-up migration with the new episode UUID and updated mapping.
--
-- If rows do not yet exist (seed was skipped at catalog activation time), the
-- UPDATE affects 0 rows, which is correct; the seeder in
-- game_catalog_competitive_extension.sql will populate them on next activation
-- and icon_url will remain NULL until this migration replays or a backfill runs.
--
-- PRE-FLIGHT: Run on production before deploying, confirm expected count before proceeding:
-- SELECT COUNT(*) FROM public.game_catalog_rank_tiers WHERE game_slug = 'valorant' AND icon_url IS NULL;
-- Expected: 25 (all 25 Valorant rank tier rows have NULL icon_url before first run of this migration)

UPDATE public.game_catalog_rank_tiers
SET    icon_url = format(
           'https://media.valorant-api.com/competitivetiers/564d8e28-c226-3b10-8e0e-9cfc911a9ae4/%s/largeicon.png',
           sort_order + 3
       )
WHERE  game_slug = 'valorant'
  AND  icon_url IS NULL;

-- ── Valorant game role icon URLs ──────────────────────────────────────────────
--
-- Role icon URLs are embedded inside per-agent data on valorant-api.com:
--   GET /v1/agents → agent.role.displayIcon (a full CDN URL keyed by role UUID)
--
-- There is no stable, statically-determinable URL pattern for role icons:
-- the role UUIDs that form the CDN path are not present in the frontend source
-- files (useValorantCatalog.ts extracts only role.displayName, not the URL).
--
-- game_catalog_game_roles.icon_url is intentionally left NULL by this migration.
--
-- Backend action required: when activating a catalog version (or on a scheduled
-- refresh), call GET https://valorant-api.com/v1/agents?isPlayableCharacter=true,
-- extract the unique (role.uuid, role.displayIcon) pairs, and UPDATE
-- game_catalog_game_roles SET icon_url = <role.displayIcon>
-- WHERE game_slug = 'valorant' AND role_key = lower(role.displayName)
-- AND icon_url IS NULL.
--
-- Frontend fallback: static icons keyed by role_key can be bundled under
-- /public/games/valorant/roles/{role_key}.png until runtime seeding runs.
