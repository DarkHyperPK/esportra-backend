-- Valorant Skirmish map pool (A–E) for catalog skirmish_1v1 / skirmish_2v2 modes
INSERT INTO game_maps (game, map_name, is_active)
SELECT 'Valorant', map_name, true
FROM (
    VALUES
        ('Skirmish A'),
        ('Skirmish B'),
        ('Skirmish C'),
        ('Skirmish D'),
        ('Skirmish E')
) AS seed(map_name)
WHERE NOT EXISTS (
    SELECT 1
    FROM game_maps existing
    WHERE existing.game = 'Valorant'
      AND existing.map_name = seed.map_name
);
