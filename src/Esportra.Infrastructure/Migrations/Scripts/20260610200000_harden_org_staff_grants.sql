-- Harden organization staff tables: backend owns writes via service_role.
-- Authenticated users may read their own staff rows only.

DO $$
BEGIN
    IF to_regclass('public.organization_staff') IS NOT NULL THEN
        ALTER TABLE public.organization_staff ENABLE ROW LEVEL SECURITY;

        DROP POLICY IF EXISTS organization_staff_self_select ON public.organization_staff;
        CREATE POLICY organization_staff_self_select ON public.organization_staff
            FOR SELECT TO authenticated
            USING (user_id = auth.uid());

        REVOKE ALL PRIVILEGES ON public.organization_staff FROM authenticated;
        GRANT SELECT ON public.organization_staff TO authenticated;
        GRANT SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER
            ON public.organization_staff TO service_role;
    END IF;

    IF to_regclass('public.staff_tournament_assignments') IS NOT NULL THEN
        ALTER TABLE public.staff_tournament_assignments ENABLE ROW LEVEL SECURITY;

        DROP POLICY IF EXISTS staff_tournament_assignments_self_select ON public.staff_tournament_assignments;
        CREATE POLICY staff_tournament_assignments_self_select ON public.staff_tournament_assignments
            FOR SELECT TO authenticated
            USING (
                EXISTS (
                    SELECT 1
                    FROM public.organization_staff os
                    WHERE os.id = staff_tournament_assignments.organization_staff_id
                      AND os.user_id = auth.uid()
                )
            );

        REVOKE ALL PRIVILEGES ON public.staff_tournament_assignments FROM authenticated;
        GRANT SELECT ON public.staff_tournament_assignments TO authenticated;
        GRANT SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER
            ON public.staff_tournament_assignments TO service_role;
    END IF;
END;
$$;

-- Revoke direct authenticated EXECUTE on legacy dispute RPCs if they still exist.
DO $$
DECLARE
    fn RECORD;
BEGIN
    FOR fn IN
        SELECT p.oid::regprocedure AS signature
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = 'public'
          AND p.proname IN ('notify_admins_of_dispute', 'file_match_result_dispute')
    LOOP
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM authenticated', fn.signature);
        EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO service_role', fn.signature);
    END LOOP;
END;
$$;
