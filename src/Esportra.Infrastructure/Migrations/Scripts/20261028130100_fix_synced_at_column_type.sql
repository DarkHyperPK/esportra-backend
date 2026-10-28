-- 20261028130100_fix_synced_at_column_type.sql
-- If player_game_stats.synced_at is TEXT (table pre-existed the TIMESTAMPTZ migration),
-- convert it so Dapper can parse the value as DateTimeOffset.

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'player_game_stats'
          AND column_name = 'synced_at'
          AND data_type = 'text'
    ) THEN
        ALTER TABLE public.player_game_stats
            ALTER COLUMN synced_at TYPE TIMESTAMPTZ USING synced_at::timestamptz;
    END IF;

    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'player_game_stats'
          AND column_name = 'created_at'
          AND data_type = 'text'
    ) THEN
        ALTER TABLE public.player_game_stats
            ALTER COLUMN created_at TYPE TIMESTAMPTZ USING created_at::timestamptz;
    END IF;

    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'player_game_stats'
          AND column_name = 'updated_at'
          AND data_type = 'text'
    ) THEN
        ALTER TABLE public.player_game_stats
            ALTER COLUMN updated_at TYPE TIMESTAMPTZ USING updated_at::timestamptz;
    END IF;
END $$;
