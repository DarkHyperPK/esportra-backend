-- Rename Call of Duty → Call of Duty: Black Ops 6 in game_maps
UPDATE game_maps SET game = 'Call of Duty: Black Ops 6' WHERE game = 'Call of Duty';

-- Rename EA FC 25 → EA FC in game_maps (if any exist)
UPDATE game_maps SET game = 'EA FC' WHERE game = 'EA FC 25';

-- Also update tournaments table so existing tournaments match
UPDATE tournaments SET game = 'Call of Duty: Black Ops 6' WHERE game = 'Call of Duty';
UPDATE tournaments SET game = 'EA FC' WHERE game = 'EA FC 25';
