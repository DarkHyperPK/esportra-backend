-- Upsert EA FC 2v2 team mode into the active game catalog (admin or packaged).

DO $$
DECLARE
    v_version_id UUID;
BEGIN
    SELECT id INTO v_version_id
    FROM public.game_catalog_versions
    WHERE is_active = TRUE AND status = 'active'
    LIMIT 1;

    IF v_version_id IS NULL THEN
        RAISE NOTICE 'No active game catalog version; skipping EA FC 2v2 upsert.';
        RETURN;
    END IF;

    INSERT INTO public.game_catalog_game_modes
        (version_id, game_slug, mode_key, name, team_size, participant_mode,
         allows_substitutes, max_roster_size, aliases, raw)
    VALUES
        (
            v_version_id,
            'eafc',
            '2v2',
            '2v2',
            2,
            'team',
            TRUE,
            3,
            ARRAY['2v2']::TEXT[],
            '{"name":"2v2","key":"2v2","value":"2v2","teamSize":2,"participantMode":"team","allowsSubstitutes":true,"maxRosterSize":3,"aliases":["2v2"]}'::jsonb
        )
    ON CONFLICT (version_id, game_slug, mode_key)
    DO UPDATE SET
        name = EXCLUDED.name,
        team_size = EXCLUDED.team_size,
        participant_mode = EXCLUDED.participant_mode,
        allows_substitutes = EXCLUDED.allows_substitutes,
        max_roster_size = EXCLUDED.max_roster_size,
        aliases = EXCLUDED.aliases,
        raw = EXCLUDED.raw;
END $$;
