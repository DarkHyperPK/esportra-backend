-- Normalize picked-map columns to json (Supabase schema) with empty-array default.
ALTER TABLE public.match_map_vetos
  ADD COLUMN IF NOT EXISTS team1_picked_maps json DEFAULT '[]'::json,
  ADD COLUMN IF NOT EXISTS team2_picked_maps json DEFAULT '[]'::json;

DO $$
DECLARE
  col record;
BEGIN
  FOR col IN
    SELECT column_name, udt_name, data_type
    FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name = 'match_map_vetos'
      AND column_name IN ('team1_picked_maps', 'team2_picked_maps')
  LOOP
    IF col.udt_name NOT IN ('json', 'jsonb') THEN
      EXECUTE format(
        'ALTER TABLE public.match_map_vetos ALTER COLUMN %I TYPE json USING %s',
        col.column_name,
        CASE
          WHEN col.udt_name = 'jsonb' THEN format('COALESCE(%I::text, ''[]'')::json', col.column_name)
          WHEN col.udt_name = 'json' THEN format('COALESCE(%I::text, ''[]'')::json', col.column_name)
          WHEN col.data_type = 'ARRAY' THEN format(
            'COALESCE((SELECT jsonb_agg(jsonb_build_object(''map_id'', elem::text, ''side'', null)) FROM unnest(%I) AS elem), ''[]''::jsonb)',
            col.column_name)
          WHEN col.udt_name IN ('text', 'varchar') THEN format(
            'COALESCE(NULLIF(btrim(%I), '''')::jsonb, ''[]''::jsonb)',
            col.column_name)
          ELSE '''[]''::jsonb'
        END
      );
    END IF;
  END LOOP;
END $$;

UPDATE public.match_map_vetos
   SET team1_picked_maps = '[]'::json
 WHERE team1_picked_maps IS NULL;

UPDATE public.match_map_vetos
   SET team2_picked_maps = '[]'::json
 WHERE team2_picked_maps IS NULL;

ALTER TABLE public.match_map_vetos
  ALTER COLUMN team1_picked_maps SET DEFAULT '[]'::json;

ALTER TABLE public.match_map_vetos
  ALTER COLUMN team2_picked_maps SET DEFAULT '[]'::json;
