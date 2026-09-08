-- Phase 3: scheduling escalation notification type + match escalation tracking column

ALTER TYPE notification_type ADD VALUE IF NOT EXISTS 'scheduling_escalation';

ALTER TABLE brkt_matches
    ADD COLUMN IF NOT EXISTS scheduling_escalated_at TIMESTAMPTZ NULL;
