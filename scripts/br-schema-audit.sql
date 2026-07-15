-- Battle Royale schema audit — run before/after pro-lobby migration.
-- Usage (staging SSH):
--   ssh -i <key> ubuntu@79.72.48.169 \
--     "sudo docker exec -i supabase-db-usoocgow4s0wow00gsw04kcg \
--        psql -U supabase_admin -d postgres -f -" < scripts/br-schema-audit.sql

\echo '=== Row counts ==='
SELECT 'br_groups' AS tbl, count(1) AS n FROM br_groups
UNION ALL SELECT 'br_rounds', count(1) FROM br_rounds
UNION ALL SELECT 'br_round_results', count(1) FROM br_round_results
UNION ALL SELECT 'br_round_evidence', count(1) FROM br_round_evidence
UNION ALL SELECT 'br_lobbies', count(1) FROM br_lobbies WHERE to_regclass('public.br_lobbies') IS NOT NULL
UNION ALL SELECT 'br_lobby_results', count(1) FROM br_lobby_results WHERE to_regclass('public.br_lobby_results') IS NOT NULL
UNION ALL SELECT 'br_stages', count(1) FROM tournament_stages WHERE format = 'battle_royale';

\echo '=== br_rounds columns ==='
SELECT column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_schema = 'public' AND table_name = 'br_rounds'
ORDER BY ordinal_position;

\echo '=== Post-migration gate: br_rounds must be dropped, br_lobbies must exist ==='
SELECT
  to_regclass('public.br_lobbies') IS NOT NULL AS has_br_lobbies,
  to_regclass('public.br_rounds') IS NULL AS br_rounds_dropped;
