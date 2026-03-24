-- Recreate the handle_new_user trigger that auto-creates a profile row
-- whenever a new user is inserted into auth.users.
-- This trigger was lost during prod→staging syncs because pg_dump does not
-- include triggers on auth schema tables. Adding it here ensures it is
-- re-applied on both staging and production via the migration pipeline.

CREATE OR REPLACE FUNCTION public.handle_new_user()
  RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path TO 'public'
AS $function$
BEGIN
    INSERT INTO public.profiles (id, username, full_name, email, avatar_url, role, date_of_birth)
    VALUES (
        NEW.id,
        COALESCE(NEW.raw_user_meta_data->>'username', split_part(NEW.email, '@', 1)),
        COALESCE(NEW.raw_user_meta_data->>'full_name', split_part(NEW.email, '@', 1)),
        NEW.email,
        NEW.raw_user_meta_data->>'avatar_url',
        COALESCE(NEW.raw_user_meta_data->>'role', 'casual')::app_role,
        (NEW.raw_user_meta_data->>'date_of_birth')::date
    );
    RETURN NEW;
END;
$function$;

-- Drop first to avoid "trigger already exists" error on re-runs
DROP TRIGGER IF EXISTS on_auth_user_created ON auth.users;

CREATE TRIGGER on_auth_user_created
  AFTER INSERT ON auth.users
  FOR EACH ROW EXECUTE FUNCTION public.handle_new_user();
