-- ============================================================================
--  41  Bank reconciliation can be started from the screen: the statement
--      PERIOD, and a reference on each statement line
-- ============================================================================
--
--  Applied to the local test copy only -- NOT yet run on live.
--
--  Until 27 Sep the Bank Reconciliation screen could match and finalise, but
--  nothing could START a reconciliation or put a bank statement into one: the
--  five on live were seeded, and the page said so ("statement lines come from
--  the database"). BankReconciliationController (new) creates a reconciliation,
--  imports a statement from .xlsx / .csv, and takes single lines by hand.
--
--  Two columns make that work properly:
--
--    "BankReconciliation"."PeriodFrom"  -- the first day the statement covers.
--        A reconciliation only ever had its END date (StatementDate). With a
--        start, an imported line dated outside the statement is caught at the
--        preview, and the ledger lines offered for matching are the period's
--        own instead of "a month either side of the end date". NULL on the
--        five old rows, which keep the old window.
--
--    "BankStatementLine"."Reference"    -- the bank's own reference for the
--        line (cheque number, transaction id). Bank statements carry one on
--        nearly every row; it is what tells two same-day, same-amount lines
--        apart, and the import uses it to spot a statement loaded twice.
--
--  Both nullable, additive, nothing reads them in the old build: safe to run
--  before OR after the API deploy, and safe to run twice.
--
--  Rollback:
--    ALTER TABLE "BankStatementLine"  DROP COLUMN IF EXISTS "Reference";
--    ALTER TABLE "BankReconciliation" DROP CONSTRAINT IF EXISTS "BankReconciliation_Period_check";
--    ALTER TABLE "BankReconciliation" DROP COLUMN IF EXISTS "PeriodFrom";
-- ============================================================================

BEGIN;

ALTER TABLE "BankReconciliation" ADD COLUMN IF NOT EXISTS "PeriodFrom" date NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'BankReconciliation_Period_check') THEN
        ALTER TABLE "BankReconciliation"
            ADD CONSTRAINT "BankReconciliation_Period_check"
            CHECK ("PeriodFrom" IS NULL OR "PeriodFrom" <= "StatementDate");
    END IF;
END $$;

ALTER TABLE "BankStatementLine" ADD COLUMN IF NOT EXISTS "Reference" varchar(60) NULL;

COMMIT;
