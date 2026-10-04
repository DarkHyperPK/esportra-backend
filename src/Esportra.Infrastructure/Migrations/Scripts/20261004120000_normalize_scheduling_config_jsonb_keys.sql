-- PRE-FLIGHT: Run on production before deploying, confirm expected count before proceeding:
-- SELECT COUNT(*) FROM tournament_stages
--   WHERE scheduling_config IS NOT NULL
--   AND scheduling_config ?| ARRAY[
--     'selfPlayEnabled','checkinEnabled','checkinWindowMinutes',
--     'roundDeadline','roundDeadlines','scheduleStartTime',
--     'matchIntervalMinutes','dailyStartTime','schedulingMode'
--   ];
-- Expected: any non-negative integer; all matching rows have their camelCase keys renamed

UPDATE tournament_stages t
SET scheduling_config = (
    WITH expanded AS (
        SELECT
            CASE key
                WHEN 'selfPlayEnabled'      THEN 'self_play_enabled'
                WHEN 'checkinEnabled'       THEN 'checkin_enabled'
                WHEN 'checkinWindowMinutes' THEN 'checkin_window_minutes'
                WHEN 'roundDeadline'        THEN 'round_deadline'
                WHEN 'roundDeadlines'       THEN 'round_deadlines'
                WHEN 'scheduleStartTime'    THEN 'schedule_start_time'
                WHEN 'matchIntervalMinutes' THEN 'match_interval_minutes'
                WHEN 'dailyStartTime'       THEN 'daily_start_time'
                WHEN 'schedulingMode'       THEN 'scheduling_mode'
                ELSE key
            END AS normalized_key,
            value,
            CASE WHEN key IN (
                'selfPlayEnabled','checkinEnabled','checkinWindowMinutes',
                'roundDeadline','roundDeadlines','scheduleStartTime',
                'matchIntervalMinutes','dailyStartTime','schedulingMode'
            ) THEN 1 ELSE 0 END AS priority
        FROM jsonb_each(t.scheduling_config)
    ),
    deduped AS (
        SELECT DISTINCT ON (normalized_key) normalized_key, value
        FROM expanded
        ORDER BY normalized_key, priority ASC
    )
    SELECT jsonb_object_agg(normalized_key, value)
    FROM deduped
)
WHERE scheduling_config IS NOT NULL
  AND scheduling_config ?| ARRAY[
    'selfPlayEnabled','checkinEnabled','checkinWindowMinutes',
    'roundDeadline','roundDeadlines','scheduleStartTime',
    'matchIntervalMinutes','dailyStartTime','schedulingMode'
  ];
