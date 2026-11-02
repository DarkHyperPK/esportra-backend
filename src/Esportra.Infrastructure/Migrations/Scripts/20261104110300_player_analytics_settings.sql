-- A player's choice about their Riot-derived performance analytics. No row means private: only the player and
-- the staff of the teams they played for can see detailed stats. 'public' is an explicit opt-in.
CREATE TABLE IF NOT EXISTS public.player_analytics_settings (
    user_id      uuid        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    game_slug    text        NOT NULL DEFAULT 'valorant',
    visibility   text        NOT NULL DEFAULT 'private',
    consented_at timestamptz,
    updated_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, game_slug)
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_pas_visibility') THEN
        ALTER TABLE public.player_analytics_settings
            ADD CONSTRAINT chk_pas_visibility CHECK (visibility IN ('private', 'public'));
    END IF;
END $$;

ALTER TABLE public.player_analytics_settings ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS pas_own_select ON public.player_analytics_settings;
CREATE POLICY pas_own_select ON public.player_analytics_settings
    FOR SELECT TO authenticated USING (user_id = auth.uid());
DROP POLICY IF EXISTS pas_service ON public.player_analytics_settings;
CREATE POLICY pas_service ON public.player_analytics_settings FOR ALL TO service_role USING (true) WITH CHECK (true);

-- Changes go through the API so the consent time is recorded server-side.
REVOKE ALL ON public.player_analytics_settings FROM anon;
REVOKE INSERT, UPDATE, DELETE ON public.player_analytics_settings FROM authenticated;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.player_analytics_settings TO service_role;
