-- Add missing Valorant regions: BR, MENA, SEA, OCE

DO $$
DECLARE
    v_version_id UUID;
BEGIN
    SELECT id INTO v_version_id
    FROM public.game_catalog_versions
    WHERE is_active = TRUE
    LIMIT 1;

    IF v_version_id IS NULL THEN
        RETURN;
    END IF;

    INSERT INTO public.game_catalog_regions (version_id, game_slug, region_key, display_name, server_ids)
    VALUES
        (v_version_id, 'valorant', 'br',   'BR',   ARRAY['br']),
        (v_version_id, 'valorant', 'mena', 'MENA', ARRAY['mena']),
        (v_version_id, 'valorant', 'sea',  'SEA',  ARRAY['sea']),
        (v_version_id, 'valorant', 'oce',  'OCE',  ARRAY['oce'])
    ON CONFLICT DO NOTHING;

END $$;
