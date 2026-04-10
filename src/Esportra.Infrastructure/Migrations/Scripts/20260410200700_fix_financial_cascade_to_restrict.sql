-- ============================================================================
-- Migration: 20260410200700_fix_financial_cascade_to_restrict.sql
-- Purpose:   Change ON DELETE CASCADE to ON DELETE RESTRICT on financial
--            transaction tables to preserve audit trails.
--            Deleting a wallet/loyalty account must NOT cascade-delete
--            the transaction history.
-- ============================================================================

-- ── wallet_transactions: wallet_id FK ───────────────────────────────────────
DO $$
BEGIN
    -- Drop the existing CASCADE constraint
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'wallet_transactions_wallet_id_fkey'
          AND table_name = 'wallet_transactions'
    ) THEN
        ALTER TABLE wallet_transactions DROP CONSTRAINT wallet_transactions_wallet_id_fkey;
    END IF;

    -- Re-add with RESTRICT
    ALTER TABLE wallet_transactions
        ADD CONSTRAINT wallet_transactions_wallet_id_fkey
        FOREIGN KEY (wallet_id) REFERENCES customer_wallets(id) ON DELETE RESTRICT;
END $$;

-- ── loyalty_transactions: account_id FK ─────────────────────────────────────
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'loyalty_transactions_account_id_fkey'
          AND table_name = 'loyalty_transactions'
    ) THEN
        ALTER TABLE loyalty_transactions DROP CONSTRAINT loyalty_transactions_account_id_fkey;
    END IF;

    ALTER TABLE loyalty_transactions
        ADD CONSTRAINT loyalty_transactions_account_id_fkey
        FOREIGN KEY (account_id) REFERENCES loyalty_accounts(id) ON DELETE RESTRICT;
END $$;
