-- ============================================================================
-- Migration: 20260410200400_wallet_rpc_functions.sql
-- Purpose:   Atomic wallet RPC functions to prevent race conditions.
--            Replaces client-side read-modify-write with row-locked
--            server-side operations that prevent double-spend.
-- ============================================================================

-- Atomic wallet top-up: prevents race conditions via row-level locking
CREATE OR REPLACE FUNCTION wallet_topup(
    p_venue_id UUID,
    p_user_id UUID,
    p_amount NUMERIC,
    p_currency TEXT DEFAULT 'USD',
    p_description TEXT DEFAULT 'Top-up',
    p_created_by UUID DEFAULT NULL
)
RETURNS TABLE(
    wallet_id UUID,
    new_balance NUMERIC
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
    v_wallet_id UUID;
    v_new_balance NUMERIC;
BEGIN
    -- Find or create wallet with row lock
    INSERT INTO customer_wallets (user_id, venue_id, balance, currency)
    VALUES (p_user_id, p_venue_id, 0, p_currency)
    ON CONFLICT (user_id, venue_id) DO UPDATE SET updated_at = now()
    RETURNING id INTO v_wallet_id;

    -- Atomic balance update
    UPDATE customer_wallets
    SET balance = balance + p_amount, updated_at = now()
    WHERE id = v_wallet_id
    RETURNING balance INTO v_new_balance;

    -- Record transaction
    INSERT INTO wallet_transactions (wallet_id, venue_id, type, amount, balance_after, description, created_by)
    VALUES (v_wallet_id, p_venue_id, 'topup', p_amount, v_new_balance, p_description, p_created_by);

    RETURN QUERY SELECT v_wallet_id, v_new_balance;
END;
$$;

-- Atomic wallet deduction: prevents double-spend via row-level locking
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
    -- Lock the row and check balance atomically
    SELECT balance INTO v_current_balance
    FROM customer_wallets
    WHERE id = p_wallet_id AND is_active = true
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT false, 0::NUMERIC, 'Wallet not found or inactive'::TEXT;
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

-- Grant execute to service_role and authenticated
GRANT EXECUTE ON FUNCTION wallet_topup TO service_role;
GRANT EXECUTE ON FUNCTION wallet_topup TO authenticated;
GRANT EXECUTE ON FUNCTION wallet_deduct TO service_role;
GRANT EXECUTE ON FUNCTION wallet_deduct TO authenticated;
