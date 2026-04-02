-- Add display_order column to team_members for custom playercard ordering.
-- Captain is always order 0 (enforced by application logic).

ALTER TABLE public.team_members ADD COLUMN IF NOT EXISTS display_order INT NOT NULL DEFAULT 0;
