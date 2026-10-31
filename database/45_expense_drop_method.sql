-- ============================================================================
--  45  An expense has no payment method
-- ============================================================================
--
--  The owner, 1 Nov: "from expenses page remove METHOD attribute and also from
--  database remove Method attribute while creating and fetching expenses".
--  The paid-from account already says cash or which bank. The expense sheet,
--  the expense page, both PDFs and the export no longer ask for it or show it,
--  and the API no longer reads or writes the column.
--
--  ORDER: the column is NOT NULL and the new API does not write it, so run
--  this together with the API deploy. The OLD API reads it, so it cannot open
--  expense sheets once this has run. Run the two back to back.
--
--  The existing expenses lose their method. Nothing posts from it: the entry
--  credits the paid-from account.
--
--  Rollback (the values cannot be brought back -- every expense gets Cash):
--    ALTER TABLE "Expense" ADD COLUMN "MethodId" integer NOT NULL DEFAULT 1;
--    ALTER TABLE "Expense" ALTER COLUMN "MethodId" DROP DEFAULT;
--    ALTER TABLE "Expense" ADD CONSTRAINT fk_expense_method FOREIGN KEY ("MethodId")
--      REFERENCES "PaymentMethod"("MethodId") ON UPDATE CASCADE ON DELETE CASCADE;
--
--  Idempotent: safe to run twice.
-- ============================================================================

BEGIN;

ALTER TABLE "Expense" DROP CONSTRAINT IF EXISTS fk_expense_method;
ALTER TABLE "Expense" DROP COLUMN IF EXISTS "MethodId";

COMMIT;
