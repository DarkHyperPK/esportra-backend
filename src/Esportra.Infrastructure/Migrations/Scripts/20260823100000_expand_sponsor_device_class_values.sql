-- Expand sponsor_device_daily_stats.device_class cardinality:
-- coarse families -> concrete device types detected from User-Agent.
-- Legacy values remain allowed so historical rows keep satisfying the constraint.

ALTER TABLE public.sponsor_device_daily_stats
    DROP CONSTRAINT IF EXISTS sponsor_device_daily_stats_device_class_check;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'sponsor_device_daily_stats_device_class_check'
          AND conrelid = 'public.sponsor_device_daily_stats'::regclass
    ) THEN
        ALTER TABLE public.sponsor_device_daily_stats
            ADD CONSTRAINT sponsor_device_daily_stats_device_class_check
            CHECK (device_class IN (
                'mobile-web', 'desktop-web', 'tablet-web', 'unknown-web',
                'iphone', 'ipad', 'android-phone', 'android-tablet',
                'windows-pc', 'mac', 'linux-pc'
            ));
    END IF;
END $$;
