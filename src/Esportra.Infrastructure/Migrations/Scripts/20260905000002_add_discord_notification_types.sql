-- Phase 6: Discord DM notification types for match flow events

ALTER TYPE notification_type ADD VALUE IF NOT EXISTS 'time_proposal_received';
ALTER TYPE notification_type ADD VALUE IF NOT EXISTS 'time_proposal_accepted';
ALTER TYPE notification_type ADD VALUE IF NOT EXISTS 'time_proposal_rejected';
ALTER TYPE notification_type ADD VALUE IF NOT EXISTS 'time_proposal_countered';
ALTER TYPE notification_type ADD VALUE IF NOT EXISTS 'time_proposal_expired';
ALTER TYPE notification_type ADD VALUE IF NOT EXISTS 'checkin_open';
ALTER TYPE notification_type ADD VALUE IF NOT EXISTS 'checkin_reminder';
ALTER TYPE notification_type ADD VALUE IF NOT EXISTS 'party_code_submitted';
ALTER TYPE notification_type ADD VALUE IF NOT EXISTS 'result_pending_response';
ALTER TYPE notification_type ADD VALUE IF NOT EXISTS 'match_chat_message';
