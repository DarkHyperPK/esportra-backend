-- Notification types for Squad Up player reports. ADD VALUE IF NOT EXISTS is idempotent.
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'squad_up_report_resolved';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'squad_up_warning';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'squad_up_restricted';
