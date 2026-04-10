-- ============================================================================
-- Migration: 20260410200500_revoke_delete_financial.sql
-- Purpose:   Revoke DELETE on financial tables — these should be append-only.
--            wallet_transactions is an audit trail; customer_wallets use
--            is_active flag for soft-delete instead of hard-delete.
-- ============================================================================

-- Revoke DELETE on financial tables - these should be append-only
REVOKE DELETE ON customer_wallets FROM authenticated;
REVOKE DELETE ON wallet_transactions FROM authenticated;
REVOKE DELETE ON wallet_transactions FROM service_role;

-- wallet_transactions is append-only: nobody deletes transaction history
-- customer_wallets: only service_role can deactivate (via is_active flag), no deletes
