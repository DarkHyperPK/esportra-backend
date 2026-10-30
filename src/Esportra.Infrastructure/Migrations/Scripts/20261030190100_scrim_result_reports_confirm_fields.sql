-- PROJ-077 M6: Confirm-result mechanics
-- Extends scrim_result_reports with confirmed_by_team_id and confirmed_at.
-- Confirmation is written by the opposing team after reviewing the submitted result.
-- New columns are nullable; they are set atomically when a team confirms.
-- Existing RLS (both teams' staff can read; reporting team's staff can write) covers
-- these columns automatically — no policy changes needed.

ALTER TABLE public.scrim_result_reports
    ADD COLUMN IF NOT EXISTS confirmed_by_team_id UUID        NULL,
    ADD COLUMN IF NOT EXISTS confirmed_at         TIMESTAMPTZ NULL;

-- FK: confirmed_by_team_id must reference a real team (guarded)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_scrim_result_reports_confirmed_team') THEN
        ALTER TABLE public.scrim_result_reports
            ADD CONSTRAINT fk_scrim_result_reports_confirmed_team
            FOREIGN KEY (confirmed_by_team_id) REFERENCES public.teams(id);
    END IF;
END $$;

-- confirmed_at must be set if and only if confirmed_by_team_id is set
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_result_confirm_consistency') THEN
        ALTER TABLE public.scrim_result_reports
            ADD CONSTRAINT chk_scrim_result_confirm_consistency
            CHECK (
                (confirmed_by_team_id IS NULL) = (confirmed_at IS NULL)
            );
    END IF;
END $$;
