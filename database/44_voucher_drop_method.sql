-- ============================================================================
--  44  A voucher has no payment method
-- ============================================================================
--
--  NOT yet run on live.
--
--  The owner, 27 Oct: "remove this Method entry from db table and we do not
--  need Method attribute". The voucher type already says cash or bank (CR/CP
--  against BR/BP), and "CashBankAccountId" says which account the money went
--  into or came out of -- the method only repeated that. The New Voucher form,
--  the voucher page, the voucher list and the Voucher Book no longer ask for it
--  or show it, and the API no longer reads or writes the column.
--
--  ORDER: the column is NOT NULL, and the new API does not write it, so run
--  this BEFORE (or together with) the API deploy -- until then every new
--  voucher fails to save. The API still running on the OLD code reads
--  "Voucher"."MethodId", so once this runs that old API cannot list or open
--  vouchers until the new one is deployed. Run the two back to back.
--
--  The 21 existing vouchers lose their method. Nothing else reads it: the
--  ledger is posted from the cash / bank account, not from the method.
--
--  Rollback (the values cannot be brought back, so every voucher gets Cash):
--    ALTER TABLE "Voucher" ADD COLUMN "MethodId" integer NOT NULL
--      DEFAULT (SELECT "MethodId" FROM "PaymentMethod" WHERE "MethodKey" = 'CASH');
--    -- (Postgres refuses a subquery default: look the id up and put the number in.)
--    ALTER TABLE "Voucher" ADD CONSTRAINT fk_voucher_method FOREIGN KEY ("MethodId")
--      REFERENCES "PaymentMethod"("MethodId") ON UPDATE CASCADE ON DELETE CASCADE;
--
--  Idempotent: safe to run twice.
-- ============================================================================

BEGIN;

ALTER TABLE "Voucher" DROP CONSTRAINT IF EXISTS fk_voucher_method;
ALTER TABLE "Voucher" DROP COLUMN IF EXISTS "MethodId";

COMMIT;
