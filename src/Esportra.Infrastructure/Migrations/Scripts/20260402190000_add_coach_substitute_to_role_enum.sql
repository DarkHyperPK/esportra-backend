-- Add 'substitute' and 'coach' to team_member_role enum
ALTER TYPE team_member_role ADD VALUE IF NOT EXISTS 'substitute';
ALTER TYPE team_member_role ADD VALUE IF NOT EXISTS 'coach';
