-- ============================================================================
--  49  No vendors; customers, suppliers and staff on expenses and vouchers
-- ============================================================================
--
--  The owner, 8 Nov:
--
--  VENDORS ARE GONE. "vendor ka option ko bilkul remove kr do ... pora db ma
--  jahan jahan vendor hai drop krdo column ko". "Expense"."VendorName" (the
--  typed "paid to") and "Expense"."VendorAccountId" (database/48) are dropped,
--  and the 2170 Expense Vendors heading with its one account (2171) is
--  deleted -- neither ever took a posting. The expense's description and head
--  say what it was for.
--
--  EVERY ACCOUNT ON AN EXPENSE. The head and the paid-from side may now be any
--  postable account, a customer or supplier (on 1130 / 2101) or a member of
--  staff (on 2140): "Expense"."ExpensePartyId" / "ExpenseStaffId" and
--  "PaidFromPartyId" / "PaidFromStaffId" say who, and approval writes them on
--  the journal line (PartyUserId / StaffId).
--
--  STAFF ON A VOUCHER. "Voucher"."StaffId": a receipt from, or a payment to, a
--  member of staff, posted on 2140 for them.
--
--  ORDER: the new API reads the new columns and no longer writes VendorName
--  (NOT NULL), so run this together with the API deploy. Idempotent.
--
--  Rollback (VendorName cannot be brought back; every expense gets ''):
--    ALTER TABLE "Expense" ADD COLUMN "VendorName" varchar(150) NOT NULL DEFAULT '';
--    ALTER TABLE "Expense" DROP COLUMN "ExpensePartyId", DROP COLUMN "ExpenseStaffId",
--                          DROP COLUMN "PaidFromPartyId", DROP COLUMN "PaidFromStaffId";
--    ALTER TABLE "Voucher" DROP COLUMN "StaffId";
-- ============================================================================

BEGIN;

-- vendors
ALTER TABLE "Expense" DROP CONSTRAINT IF EXISTS fk_expense_vendor_account;
ALTER TABLE "Expense" DROP COLUMN IF EXISTS "VendorAccountId";
ALTER TABLE "Expense" DROP COLUMN IF EXISTS "VendorName";

DELETE FROM "Account" a
 WHERE a."ParentAccountId" = (SELECT "AccountId" FROM "Account" WHERE "AccountCode" = '2170')
   AND NOT EXISTS (SELECT 1 FROM "JournalEntryLine" l WHERE l."AccountId" = a."AccountId");
DELETE FROM "Account" a
 WHERE a."AccountCode" = '2170'
   AND NOT EXISTS (SELECT 1 FROM "Account" c WHERE c."ParentAccountId" = a."AccountId");

-- who, on an expense
ALTER TABLE "Expense" ADD COLUMN IF NOT EXISTS "ExpensePartyId" integer NULL;
ALTER TABLE "Expense" ADD COLUMN IF NOT EXISTS "ExpenseStaffId" integer NULL;
ALTER TABLE "Expense" ADD COLUMN IF NOT EXISTS "PaidFromPartyId" integer NULL;
ALTER TABLE "Expense" ADD COLUMN IF NOT EXISTS "PaidFromStaffId" integer NULL;

-- who, on a voucher
ALTER TABLE "Voucher" ADD COLUMN IF NOT EXISTS "StaffId" integer NULL;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_expense_party') THEN
    ALTER TABLE "Expense" ADD CONSTRAINT fk_expense_party FOREIGN KEY ("ExpensePartyId")
      REFERENCES "Party"("UserId") ON UPDATE CASCADE ON DELETE SET NULL;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_expense_staff') THEN
    ALTER TABLE "Expense" ADD CONSTRAINT fk_expense_staff FOREIGN KEY ("ExpenseStaffId")
      REFERENCES "StaffMember"("StaffId") ON UPDATE CASCADE ON DELETE SET NULL;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_expense_paid_from_party') THEN
    ALTER TABLE "Expense" ADD CONSTRAINT fk_expense_paid_from_party FOREIGN KEY ("PaidFromPartyId")
      REFERENCES "Party"("UserId") ON UPDATE CASCADE ON DELETE SET NULL;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_expense_paid_from_staff') THEN
    ALTER TABLE "Expense" ADD CONSTRAINT fk_expense_paid_from_staff FOREIGN KEY ("PaidFromStaffId")
      REFERENCES "StaffMember"("StaffId") ON UPDATE CASCADE ON DELETE SET NULL;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_voucher_staff') THEN
    ALTER TABLE "Voucher" ADD CONSTRAINT fk_voucher_staff FOREIGN KEY ("StaffId")
      REFERENCES "StaffMember"("StaffId") ON UPDATE CASCADE ON DELETE SET NULL;
  END IF;
END $$;

COMMIT;
