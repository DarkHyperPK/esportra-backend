-- Final grant hardening for tournament invitation feature tables.
-- Backend APIs own writes; authenticated direct access is read-only through RLS policies.
-- Skips tables that are not yet deployed (e.g. prod before invitations ship).

DO $$
DECLARE
    table_name TEXT;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'tournament_invitations'
    ]
    LOOP
        IF to_regclass(format('public.%I', table_name)) IS NULL THEN
            RAISE NOTICE 'Skipping grants for missing table public.%', table_name;
            CONTINUE;
        END IF;

        EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON public.%I TO service_role', table_name);
        EXECUTE format('REVOKE ALL PRIVILEGES ON public.%I FROM authenticated', table_name);
        EXECUTE format('GRANT SELECT ON public.%I TO authenticated', table_name);
    END LOOP;
END;
$$;
