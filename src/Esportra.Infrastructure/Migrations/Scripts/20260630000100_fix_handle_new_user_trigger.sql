-- Fix privilege escalation: handle_new_user trigger previously trusted
-- raw_user_meta_data->>'role' from client signup metadata.
-- Now always forces 'casual'. Role upgrades only happen via admin workflows.

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
        'casual'::app_role,
        (NEW.raw_user_meta_data->>'date_of_birth')::date
    );
    RETURN NEW;
END;
$function$;
