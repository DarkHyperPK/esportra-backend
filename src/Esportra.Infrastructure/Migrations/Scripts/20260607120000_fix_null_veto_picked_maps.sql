-- Backfill NULL picked-map JSONB columns so map pick appends do not violate NOT NULL.
UPDATE public.match_map_vetos
   SET team1_picked_maps = '[]'::jsonb
 WHERE team1_picked_maps IS NULL;

UPDATE public.match_map_vetos
   SET team2_picked_maps = '[]'::jsonb
 WHERE team2_picked_maps IS NULL;

ALTER TABLE public.match_map_vetos
  ALTER COLUMN team1_picked_maps SET DEFAULT '[]'::jsonb;

ALTER TABLE public.match_map_vetos
  ALTER COLUMN team2_picked_maps SET DEFAULT '[]'::jsonb;
