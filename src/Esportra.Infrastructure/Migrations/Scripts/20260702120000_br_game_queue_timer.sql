-- Per-game queue timer (replaces lobby-level queue for multi-game lobbies).

ALTER TABLE br_games
    ADD COLUMN IF NOT EXISTS queue_timer_minutes INT,
    ADD COLUMN IF NOT EXISTS queue_started_at TIMESTAMPTZ;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'br_games_queue_timer_minutes_check'
    ) THEN
        ALTER TABLE br_games
            ADD CONSTRAINT br_games_queue_timer_minutes_check
            CHECK (
                queue_timer_minutes IS NULL
                OR (queue_timer_minutes >= 0 AND queue_timer_minutes <= 180)
            );
    END IF;
END $$;

-- Copy legacy lobby queue to game 1 for existing rows.
UPDATE br_games g
SET queue_timer_minutes = l.queue_timer_minutes
FROM br_lobbies l
WHERE g.lobby_id = l.id
  AND g.game_number = 1
  AND g.queue_timer_minutes IS NULL
  AND l.queue_timer_minutes IS NOT NULL;
