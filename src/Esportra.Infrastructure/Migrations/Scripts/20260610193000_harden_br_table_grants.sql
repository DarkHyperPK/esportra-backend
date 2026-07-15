-- Harden Battle Royale table grants: backend API owns writes via service_role.
-- Authenticated users retain read-only access through SELECT policies.

DO $$
DECLARE
    table_name TEXT;
    suffix TEXT;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'br_groups',
        'br_group_teams',
        'br_rounds',
        'br_round_results',
        'br_round_evidence'
    ]
    LOOP
        IF to_regclass(format('public.%I', table_name)) IS NULL THEN
            RAISE NOTICE 'Skipping missing table public.%', table_name;
            CONTINUE;
        END IF;

        FOREACH suffix IN ARRAY ARRAY['insert', 'update', 'delete']
        LOOP
            EXECUTE format(
                'DROP POLICY IF EXISTS %I ON public.%I',
                table_name || '_authenticated_' || suffix,
                table_name);
        END LOOP;

        EXECUTE format(
            'GRANT SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON public.%I TO service_role',
            table_name);
        EXECUTE format('REVOKE ALL PRIVILEGES ON public.%I FROM authenticated', table_name);
        EXECUTE format('GRANT SELECT ON public.%I TO authenticated', table_name);
    END LOOP;
END;
$$;
