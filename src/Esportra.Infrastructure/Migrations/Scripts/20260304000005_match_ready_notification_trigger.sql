-- Notify both team captains when a bracket match becomes ready (both teams assigned).
-- Fires on INSERT (first-round matches seeded with both teams) AND
-- on UPDATE (subsequent rounds where a team slot is filled after bracket advancement).

CREATE OR REPLACE FUNCTION public.notify_match_ready()
RETURNS TRIGGER
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
DECLARE
    rec RECORD;
BEGIN
    -- Only proceed when both teams are now assigned
    IF NEW.team1_id IS NULL OR NEW.team2_id IS NULL THEN
        RETURN NEW;
    END IF;

    -- On UPDATE: only fire when a previously-null slot was just filled
    IF TG_OP = 'UPDATE' THEN
        IF OLD.team1_id IS NOT NULL AND OLD.team2_id IS NOT NULL THEN
            RETURN NEW;   -- already had both teams — skip
        END IF;
    END IF;

    -- Fetch both team captains and send notifications
    FOR rec IN
        SELECT tm.user_id
        FROM   public.team_members tm
        WHERE  tm.team_id IN (NEW.team1_id, NEW.team2_id)
          AND  tm.role = 'captain'
          AND  tm.is_active = true
    LOOP
        INSERT INTO public.notifications
            (user_id, type, title, message, link, data, is_read)
        VALUES
            (rec.user_id, 'match_ready',
             'Match Ready',
             'Your match is ready. Head to the Captain dashboard to start the map veto.',
             '/tournaments/captain',
             jsonb_build_object('match_id', NEW.id),
             false);
    END LOOP;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_match_ready_notify ON public.brkt_matches;

CREATE TRIGGER trg_match_ready_notify
    AFTER INSERT OR UPDATE OF team1_id, team2_id ON public.brkt_matches
    FOR EACH ROW
    EXECUTE FUNCTION public.notify_match_ready();
