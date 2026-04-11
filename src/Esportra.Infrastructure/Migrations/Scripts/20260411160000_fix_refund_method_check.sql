-- Fix CHECK constraint on refund_method column
-- The original CHECK (refund_method IN ('wallet', 'cash', NULL)) doesn't properly handle NULLs.
-- PostgreSQL CHECK constraints pass on NULL by default, but the syntax is misleading.
-- This replaces it with a properly expressed constraint.

DO $$ BEGIN
  -- Drop the existing auto-named constraint if present
  IF EXISTS (
    SELECT 1 FROM information_schema.check_constraints cc
    JOIN information_schema.constraint_column_usage ccu ON cc.constraint_name = ccu.constraint_name
    WHERE ccu.table_name = 'venue_sessions' AND ccu.column_name = 'refund_method'
  ) THEN
    EXECUTE format(
      'ALTER TABLE venue_sessions DROP CONSTRAINT %I',
      (SELECT cc.constraint_name FROM information_schema.check_constraints cc
       JOIN information_schema.constraint_column_usage ccu ON cc.constraint_name = ccu.constraint_name
       WHERE ccu.table_name = 'venue_sessions' AND ccu.column_name = 'refund_method'
       LIMIT 1)
    );
  END IF;
END $$;

ALTER TABLE venue_sessions
  ADD CONSTRAINT venue_sessions_refund_method_check
  CHECK (refund_method IS NULL OR refund_method IN ('wallet', 'cash'));
