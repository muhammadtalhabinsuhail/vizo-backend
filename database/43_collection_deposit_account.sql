-- ============================================================================
--  43  A collection is received INTO an account picked from the chart
-- ============================================================================
--
--  Applied to the local test copy only -- NOT yet run on live.
--
--  The owner, 2 Oct: on Confirm Collections, the "Collect" modal offered a
--  fixed list (Cash, Bank, Easypaisa, JazzCash ...) -- payment METHODS mapped to
--  accounts by a switch in LedgerPosting.CashAccountCodeFor. A bank account
--  added to the chart (Bank Alfalah, say) could never be picked. The modal now
--  lists every active account of type "Cash & Bank", straight from "Account",
--  and the receipt is posted to the one chosen.
--
--  "Collection"."DepositAccountId" records that choice. NULL on every older
--  collection, which keeps posting through its method as before. The method
--  column stays (it is NOT NULL and the reports read it) -- the API works it
--  out from the account picked.
--
--  The API writes this column, so run it BEFORE the API deploy. Additive and
--  idempotent: safe to run twice.
--
--  Rollback:
--    ALTER TABLE "Collection" DROP COLUMN IF EXISTS "DepositAccountId";
-- ============================================================================

BEGIN;

ALTER TABLE "Collection" ADD COLUMN IF NOT EXISTS "DepositAccountId" integer NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'Collection_DepositAccountId_fkey') THEN
        ALTER TABLE "Collection" ADD CONSTRAINT "Collection_DepositAccountId_fkey"
            FOREIGN KEY ("DepositAccountId") REFERENCES "Account"("AccountId");
    END IF;
END $$;

COMMIT;
