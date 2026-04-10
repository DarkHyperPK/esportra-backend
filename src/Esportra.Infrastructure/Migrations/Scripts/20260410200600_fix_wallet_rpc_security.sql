-- ============================================================================
-- Migration: 20260410200600_fix_wallet_rpc_security.sql
-- Purpose:   Critical security fixes for wallet RPC functions:
--            1. REVOKE EXECUTE from authenticated — prevents any logged-in user
--               from calling wallet_topup/wallet_deduct directly via PostgREST,
--               bypassing application-level staff gates.
--            2. Fix wallet_deduct to verify venue_id matches the wallet's venue,
--               preventing cross-venue deduction attacks.
-- ============================================================================

-- ── 1. Revoke direct RPC access from authenticated role ─────────────────────
-- Only service_role (used by the backend API and venue hub) should call these.
REVOKE EXECUTE ON FUNCTION wallet_topup(UUID, UUID, NUMERIC, TEXT, TEXT, UUID) FROM authenticated;
REVOKE EXECUTE ON FUNCTION wallet_deduct(UUID, UUID, NUMERIC, TEXT, TEXT, UUID) FROM authenticated;

-- ── 2. Fix wallet_deduct: add venue_id check to prevent cross-venue deduction ─
CREATE OR REPLACE FUNCTION wallet_deduct(
    p_wallet_id UUID,
    p_venue_id UUID,
    p_amount NUMERIC,
    p_description TEXT DEFAULT 'Session payment',
    p_reference_id TEXT DEFAULT NULL,
    p_created_by UUID DEFAULT NULL
)
RETURNS TABLE(
    success BOOLEAN,
    new_balance NUMERIC,
    error_message TEXT
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
    v_current_balance NUMERIC;
    v_new_balance NUMERIC;
BEGIN
    -- Lock the row and check balance atomically — now also verifies venue_id
    SELECT balance INTO v_current_balance
    FROM customer_wallets
    WHERE id = p_wallet_id AND venue_id = p_venue_id AND is_active = true
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT false, 0::NUMERIC, 'Wallet not found, inactive, or venue mismatch'::TEXT;
        RETURN;
    END IF;

    IF v_current_balance < p_amount THEN
        RETURN QUERY SELECT false, v_current_balance, 'Insufficient balance'::TEXT;
        RETURN;
    END IF;

    -- Atomic deduction
    v_new_balance := v_current_balance - p_amount;
    UPDATE customer_wallets
    SET balance = v_new_balance, updated_at = now()
    WHERE id = p_wallet_id;

    -- Record transaction
    INSERT INTO wallet_transactions (wallet_id, venue_id, type, amount, balance_after, description, reference_id, created_by)
    VALUES (p_wallet_id, p_venue_id, 'deduct', p_amount, v_new_balance, p_description, p_reference_id, p_created_by);

    RETURN QUERY SELECT true, v_new_balance, NULL::TEXT;
END;
$$;

-- Re-grant to service_role only (not authenticated)
GRANT EXECUTE ON FUNCTION wallet_deduct(UUID, UUID, NUMERIC, TEXT, TEXT, UUID) TO service_role;
