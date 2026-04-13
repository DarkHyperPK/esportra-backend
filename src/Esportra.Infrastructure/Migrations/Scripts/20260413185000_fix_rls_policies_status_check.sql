-- ============================================================================
-- Migration: 20260413185000_fix_rls_policies_status_check.sql
-- Purpose:   SECURITY FIX — Replace `venue_staff.accepted_at IS NOT NULL`
--            with `venue_staff.status = 'active'` in all RLS policies.
--
-- Why:       The original policies checked only whether a staff member had
--            accepted their invitation (accepted_at IS NOT NULL).  This meant
--            suspended or terminated staff — who still have an accepted_at
--            timestamp — continued to pass RLS checks and could read/write
--            venue data they should no longer access.
--
--            The venue_staff table gained a `status` column (values: active,
--            pending, suspended, terminated) in migration 20260413220000.
--            This migration retroactively tightens every staff-facing RLS
--            policy to require `status = 'active'`.
--
-- Affected:  23 policies across 12 tables (see list below).
-- Pattern:   DROP POLICY IF EXISTS + CREATE POLICY (idempotent).
-- Rollback:  Re-create each policy with the original accepted_at IS NOT NULL
--            clause.  A rollback script is NOT included — reversing a security
--            fix should be a deliberate, manual decision.
-- ============================================================================

BEGIN;

-- ════════════════════════════════════════════════════════════════════════════
-- 1. venue_menu_items  (3 policies)
-- ════════════════════════════════════════════════════════════════════════════

-- 1a. venue_menu_items_staff_select
DROP POLICY IF EXISTS venue_menu_items_staff_select ON venue_menu_items;
CREATE POLICY venue_menu_items_staff_select ON venue_menu_items
  FOR SELECT USING (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = venue_menu_items.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- 1b. venue_menu_items_staff_insert
DROP POLICY IF EXISTS venue_menu_items_staff_insert ON venue_menu_items;
CREATE POLICY venue_menu_items_staff_insert ON venue_menu_items
  FOR INSERT WITH CHECK (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = venue_menu_items.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- 1c. venue_menu_items_staff_update
DROP POLICY IF EXISTS venue_menu_items_staff_update ON venue_menu_items;
CREATE POLICY venue_menu_items_staff_update ON venue_menu_items
  FOR UPDATE USING (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = venue_menu_items.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  ) WITH CHECK (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = venue_menu_items.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- ════════════════════════════════════════════════════════════════════════════
-- 2. venue_combos  (3 policies)
-- ════════════════════════════════════════════════════════════════════════════

-- 2a. venue_combos_staff_select
DROP POLICY IF EXISTS venue_combos_staff_select ON venue_combos;
CREATE POLICY venue_combos_staff_select ON venue_combos
  FOR SELECT USING (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = venue_combos.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- 2b. venue_combos_staff_insert
DROP POLICY IF EXISTS venue_combos_staff_insert ON venue_combos;
CREATE POLICY venue_combos_staff_insert ON venue_combos
  FOR INSERT WITH CHECK (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = venue_combos.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- 2c. venue_combos_staff_update
DROP POLICY IF EXISTS venue_combos_staff_update ON venue_combos;
CREATE POLICY venue_combos_staff_update ON venue_combos
  FOR UPDATE USING (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = venue_combos.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  ) WITH CHECK (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = venue_combos.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- ════════════════════════════════════════════════════════════════════════════
-- 3. venue_announcements  (3 policies)
-- ════════════════════════════════════════════════════════════════════════════

-- 3a. venue_announcements_staff_select
DROP POLICY IF EXISTS venue_announcements_staff_select ON venue_announcements;
CREATE POLICY venue_announcements_staff_select ON venue_announcements
  FOR SELECT USING (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = venue_announcements.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- 3b. venue_announcements_staff_insert
DROP POLICY IF EXISTS venue_announcements_staff_insert ON venue_announcements;
CREATE POLICY venue_announcements_staff_insert ON venue_announcements
  FOR INSERT WITH CHECK (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = venue_announcements.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- 3c. venue_announcements_staff_update
DROP POLICY IF EXISTS venue_announcements_staff_update ON venue_announcements;
CREATE POLICY venue_announcements_staff_update ON venue_announcements
  FOR UPDATE USING (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = venue_announcements.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  ) WITH CHECK (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = venue_announcements.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- ════════════════════════════════════════════════════════════════════════════
-- 4. loyalty_accounts  (1 policy)
-- ════════════════════════════════════════════════════════════════════════════

-- 4a. loyalty_accounts_staff_select
--     (joins through loyalty_transactions to find staff's venue)
DROP POLICY IF EXISTS loyalty_accounts_staff_select ON loyalty_accounts;
CREATE POLICY loyalty_accounts_staff_select ON loyalty_accounts
  FOR SELECT USING (
    EXISTS (
      SELECT 1 FROM loyalty_transactions lt
      JOIN venue_staff vs ON vs.venue_id = lt.venue_id
      WHERE lt.account_id      = loyalty_accounts.id
        AND vs.user_id         = auth.uid()
        AND vs.status          = 'active'
    )
  );

-- ════════════════════════════════════════════════════════════════════════════
-- 5. loyalty_transactions  (1 policy)
-- ════════════════════════════════════════════════════════════════════════════

-- 5a. loyalty_txn_staff_select
DROP POLICY IF EXISTS loyalty_txn_staff_select ON loyalty_transactions;
CREATE POLICY loyalty_txn_staff_select ON loyalty_transactions
  FOR SELECT USING (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = loyalty_transactions.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- ════════════════════════════════════════════════════════════════════════════
-- 6. venue_loyalty_config  (1 policy)
-- ════════════════════════════════════════════════════════════════════════════

-- 6a. venue_loyalty_config_staff_select
DROP POLICY IF EXISTS venue_loyalty_config_staff_select ON venue_loyalty_config;
CREATE POLICY venue_loyalty_config_staff_select ON venue_loyalty_config
  FOR SELECT USING (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = venue_loyalty_config.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- ════════════════════════════════════════════════════════════════════════════
-- 7. customer_wallets  (3 policies)
-- ════════════════════════════════════════════════════════════════════════════

-- 7a. customer_wallets_staff_select
DROP POLICY IF EXISTS customer_wallets_staff_select ON customer_wallets;
CREATE POLICY customer_wallets_staff_select ON customer_wallets
  FOR SELECT USING (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = customer_wallets.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- 7b. customer_wallets_staff_insert
DROP POLICY IF EXISTS customer_wallets_staff_insert ON customer_wallets;
CREATE POLICY customer_wallets_staff_insert ON customer_wallets
  FOR INSERT WITH CHECK (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = customer_wallets.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- 7c. customer_wallets_staff_update
DROP POLICY IF EXISTS customer_wallets_staff_update ON customer_wallets;
CREATE POLICY customer_wallets_staff_update ON customer_wallets
  FOR UPDATE USING (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = customer_wallets.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  ) WITH CHECK (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = customer_wallets.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- ════════════════════════════════════════════════════════════════════════════
-- 8. wallet_transactions  (2 policies)
-- ════════════════════════════════════════════════════════════════════════════

-- 8a. wallet_txn_staff_select
DROP POLICY IF EXISTS wallet_txn_staff_select ON wallet_transactions;
CREATE POLICY wallet_txn_staff_select ON wallet_transactions
  FOR SELECT USING (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = wallet_transactions.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- 8b. wallet_txn_staff_insert
DROP POLICY IF EXISTS wallet_txn_staff_insert ON wallet_transactions;
CREATE POLICY wallet_txn_staff_insert ON wallet_transactions
  FOR INSERT WITH CHECK (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = wallet_transactions.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- ════════════════════════════════════════════════════════════════════════════
-- 9. session_refunds  (1 policy)
-- ════════════════════════════════════════════════════════════════════════════

-- 9a. session_refunds_staff_select
DROP POLICY IF EXISTS session_refunds_staff_select ON session_refunds;
CREATE POLICY session_refunds_staff_select ON session_refunds
  FOR SELECT USING (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = session_refunds.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- ════════════════════════════════════════════════════════════════════════════
-- 10. session_invoices  (1 policy)
--     (combined owner OR staff check — only the staff branch changes)
-- ════════════════════════════════════════════════════════════════════════════

-- 10a. session_invoices_owner_select
DROP POLICY IF EXISTS session_invoices_owner_select ON session_invoices;
CREATE POLICY session_invoices_owner_select ON session_invoices
  FOR SELECT USING (
    EXISTS (
      SELECT 1 FROM venues
      WHERE venues.id       = session_invoices.venue_id
        AND venues.owner_id = auth.uid()
    )
    OR EXISTS (
      SELECT 1 FROM venue_staff
      WHERE venue_staff.venue_id = session_invoices.venue_id
        AND venue_staff.user_id  = auth.uid()
        AND venue_staff.status   = 'active'
    )
  );

-- ════════════════════════════════════════════════════════════════════════════
-- 11. zones  (1 policy)
--     (combined owner OR staff check — only the staff branch changes)
-- ════════════════════════════════════════════════════════════════════════════

-- 11a. zones_owner_select
DROP POLICY IF EXISTS zones_owner_select ON zones;
CREATE POLICY zones_owner_select ON zones
  FOR SELECT USING (
    EXISTS (
      SELECT 1 FROM venues
      WHERE venues.id       = zones.venue_id
        AND venues.owner_id = auth.uid()
    )
    OR EXISTS (
      SELECT 1 FROM venue_staff
      WHERE venue_staff.venue_id = zones.venue_id
        AND venue_staff.user_id  = auth.uid()
        AND venue_staff.status   = 'active'
    )
  );

-- ════════════════════════════════════════════════════════════════════════════
-- 12. pos_orders  (3 policies)
-- ════════════════════════════════════════════════════════════════════════════

-- 12a. pos_orders_staff_select
DROP POLICY IF EXISTS pos_orders_staff_select ON pos_orders;
CREATE POLICY pos_orders_staff_select ON pos_orders
  FOR SELECT USING (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = pos_orders.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- 12b. pos_orders_staff_insert
DROP POLICY IF EXISTS pos_orders_staff_insert ON pos_orders;
CREATE POLICY pos_orders_staff_insert ON pos_orders
  FOR INSERT WITH CHECK (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = pos_orders.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

-- 12c. pos_orders_staff_update
DROP POLICY IF EXISTS pos_orders_staff_update ON pos_orders;
CREATE POLICY pos_orders_staff_update ON pos_orders
  FOR UPDATE USING (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = pos_orders.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  ) WITH CHECK (
    EXISTS (
      SELECT 1 FROM venue_staff vs
      WHERE vs.venue_id = pos_orders.venue_id
        AND vs.user_id  = auth.uid()
        AND vs.status   = 'active'
    )
  );

COMMIT;
