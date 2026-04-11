-- =============================================================================
-- Auto-earn loyalty points when a venue session ends
-- Trigger fires when ended_at transitions from NULL → non-NULL
-- =============================================================================

-- ── Function ────────────────────────────────────────────────────────────────
CREATE OR REPLACE FUNCTION auto_earn_loyalty_on_session_end()
RETURNS TRIGGER
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
    v_config       RECORD;
    v_duration_hrs NUMERIC;
    v_raw_points   NUMERIC;
    v_points       INTEGER;
    v_account_id   UUID;
    v_new_tier     TEXT;
    v_tiers        JSONB;
    v_tier         RECORD;
BEGIN
    -- Only fire when ended_at is set for the first time
    IF NEW.ended_at IS NULL OR OLD.ended_at IS NOT NULL THEN
        RETURN NEW;
    END IF;

    -- Skip sessions without a linked user (anonymous walk-ins)
    IF NEW.user_id IS NULL THEN
        RETURN NEW;
    END IF;

    -- Get venue loyalty config (must be active)
    SELECT points_per_hour, bonus_multiplier, min_session_minutes, tiers, is_active
      INTO v_config
      FROM venue_loyalty_config
     WHERE venue_id = NEW.venue_id;

    IF NOT FOUND OR v_config.is_active IS NOT TRUE THEN
        RETURN NEW;
    END IF;

    -- Calculate session duration in hours
    v_duration_hrs := EXTRACT(EPOCH FROM (NEW.ended_at - NEW.started_at)) / 3600.0;

    -- Must meet minimum session duration
    IF (v_duration_hrs * 60) < v_config.min_session_minutes THEN
        RETURN NEW;
    END IF;

    -- Calculate points: ceil(hours) × pointsPerHour × bonusMultiplier
    v_raw_points := CEIL(v_duration_hrs) * v_config.points_per_hour * v_config.bonus_multiplier;
    v_points := GREATEST(v_raw_points::INTEGER, 1);

    -- Create or update loyalty account
    INSERT INTO loyalty_accounts (user_id, total_points, lifetime_points, current_tier)
    VALUES (NEW.user_id, v_points, v_points, 'bronze')
    ON CONFLICT (user_id) DO UPDATE SET
        total_points    = loyalty_accounts.total_points + EXCLUDED.total_points,
        lifetime_points = loyalty_accounts.lifetime_points + EXCLUDED.lifetime_points,
        updated_at      = NOW()
    RETURNING id INTO v_account_id;

    -- Record the earn transaction
    INSERT INTO loyalty_transactions (account_id, venue_id, type, points, description, session_id)
    VALUES (
        v_account_id,
        NEW.venue_id,
        'earn',
        v_points,
        'Auto-earned: ' || ROUND(v_duration_hrs, 1) || ' hrs × '
            || v_config.points_per_hour || ' pts/hr × '
            || v_config.bonus_multiplier || 'x',
        NEW.id
    );

    -- Auto-upgrade tier based on lifetime_points
    v_tiers := v_config.tiers;
    IF v_tiers IS NOT NULL AND jsonb_typeof(v_tiers) = 'array' THEN
        v_new_tier := NULL;
        FOR v_tier IN
            SELECT t->>'name' AS name, (t->>'min_points')::INTEGER AS min_points
              FROM jsonb_array_elements(v_tiers) AS t
             ORDER BY (t->>'min_points')::INTEGER DESC
        LOOP
            SELECT lifetime_points INTO v_raw_points
              FROM loyalty_accounts WHERE id = v_account_id;

            IF v_raw_points >= v_tier.min_points THEN
                v_new_tier := v_tier.name;
                EXIT;
            END IF;
        END LOOP;

        IF v_new_tier IS NOT NULL THEN
            UPDATE loyalty_accounts
               SET current_tier = LOWER(v_new_tier), updated_at = NOW()
             WHERE id = v_account_id
               AND LOWER(current_tier) IS DISTINCT FROM LOWER(v_new_tier);
        END IF;
    END IF;

    RETURN NEW;
END;
$$;

-- ── Trigger ─────────────────────────────────────────────────────────────────
DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger WHERE tgname = 'trg_auto_earn_loyalty'
    ) THEN
        CREATE TRIGGER trg_auto_earn_loyalty
            AFTER UPDATE OF ended_at ON venue_sessions
            FOR EACH ROW
            WHEN (OLD.ended_at IS NULL AND NEW.ended_at IS NOT NULL)
            EXECUTE FUNCTION auto_earn_loyalty_on_session_end();
    END IF;
END $$;
