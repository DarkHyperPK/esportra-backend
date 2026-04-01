-- Remove Rainbow Six Siege and Call of Duty: Black Ops 6 games and their maps
DELETE FROM game_maps WHERE game IN ('Rainbow Six Siege', 'Call of Duty: Black Ops 6', 'Call of Duty');
DELETE FROM games_metadata WHERE game_name IN ('Rainbow Six Siege', 'Call of Duty: Black Ops 6', 'Call of Duty');
