-- Adds the Riot map code to game_maps so ValorantMaps can resolve paths from the DB
-- instead of depending solely on valorant-api.com.
ALTER TABLE public.game_maps
    ADD COLUMN IF NOT EXISTS map_provider_code TEXT;

-- Backfill the 15 known Valorant maps (12 competitive + 3 Team Deathmatch).
-- Matches on lower(map_name) so casing differences are safe.
UPDATE public.game_maps
SET map_provider_code = v.code
FROM (VALUES
    ('Ascent',   'Ascent'),
    ('Bind',     'Duality'),
    ('Haven',    'Triad'),
    ('Split',    'Bonsai'),
    ('Icebox',   'Port'),
    ('Breeze',   'Foxtrot'),
    ('Fracture', 'Canyon'),
    ('Pearl',    'Pitt'),
    ('Lotus',    'Jam'),
    ('Sunset',   'Juliett'),
    ('Abyss',    'Infinity'),
    ('Corrode',  'Rook'),
    ('District', 'HURM_Alley'),
    ('Kasbah',   'HURM_Bowl'),
    ('Piazza',   'HURM_Yard')
) AS v(map_name, code)
WHERE lower(public.game_maps.game) = 'valorant'
  AND lower(public.game_maps.map_name) = lower(v.map_name)
  AND public.game_maps.map_provider_code IS NULL;

CREATE INDEX IF NOT EXISTS idx_game_maps_provider_code
    ON public.game_maps (map_provider_code)
    WHERE map_provider_code IS NOT NULL;
