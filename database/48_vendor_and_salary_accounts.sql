-- ============================================================================
--  48  Vendor accounts for expenses; a salary account per member of staff
-- ============================================================================
--
--  The owner, 1 Nov:
--
--  VENDORS. "If I take electricity from XYZ company I must be allowed to create
--  a vendor account, and when I add the expense that I paid for electricity,
--  XYZ's account must be credited." Vendor accounts are opened under a new
--  heading, 2170 Expense Vendors (Current Liabilities). An expense line that
--  names one ("Expense"."VendorAccountId") posts its bill and its payment
--  through that account on approval:
--      Dr expense head        Cr vendor     (the bill)
--      Dr vendor              Cr cash/bank  (the payment)
--  so the vendor's ledger shows every bill and payment and nets to what is
--  still owed -- nothing, for an expense paid on the day.
--
--  SALARY ACCOUNTS. "Not only general SALARY -- SHAHAB SALARY, and more
--  employees' salary accounts in future, connected with the staff ledger."
--  "StaffMember"."SalaryAccountId" links a member of staff to their own
--  expense account. An expense booked to it goes through 2140 Staff Payables
--  for that person (salary due, then paid), so it lands on their staff ledger;
--  and the staff ledger's salary rows and the monthly salary run post to it
--  instead of the general 5101.
--
--  The API reads both columns: run this BEFORE (or with) the API deploy.
--  Additive and idempotent.
--
--  Rollback:
--    ALTER TABLE "Expense" DROP COLUMN IF EXISTS "VendorAccountId";
--    ALTER TABLE "StaffMember" DROP COLUMN IF EXISTS "SalaryAccountId";
--    DELETE FROM "Account" WHERE "AccountCode" = '2170'
--      AND NOT EXISTS (SELECT 1 FROM "Account" c WHERE c."ParentAccountId" = "Account"."AccountId");
-- ============================================================================

BEGIN;

ALTER TABLE "Expense" ADD COLUMN IF NOT EXISTS "VendorAccountId" integer NULL;
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_expense_vendor_account') THEN
    ALTER TABLE "Expense" ADD CONSTRAINT fk_expense_vendor_account FOREIGN KEY ("VendorAccountId")
      REFERENCES "Account"("AccountId") ON UPDATE CASCADE ON DELETE SET NULL;
  END IF;
END $$;

ALTER TABLE "StaffMember" ADD COLUMN IF NOT EXISTS "SalaryAccountId" integer NULL;
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_staff_salary_account') THEN
    ALTER TABLE "StaffMember" ADD CONSTRAINT fk_staff_salary_account FOREIGN KEY ("SalaryAccountId")
      REFERENCES "Account"("AccountId") ON UPDATE CASCADE ON DELETE SET NULL;
  END IF;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS ux_staff_salary_account ON "StaffMember" ("SalaryAccountId")
  WHERE "SalaryAccountId" IS NOT NULL;

-- the id sequence may trail rows inserted with explicit ids
SELECT setval(pg_get_serial_sequence('"Account"', 'AccountId'),
              GREATEST((SELECT MAX("AccountId") FROM "Account"), 1));

INSERT INTO "Account" ("AccountCode", "AccountName", "ParentAccountId", "AccountTypeId", "IsGroup",
                       "OpeningBalance", "CurrencyCode", "IsActive")
SELECT '2170', 'Expense Vendors',
       (SELECT "AccountId" FROM "Account" WHERE "AccountCode" = '2100'),
       (SELECT "AccountTypeId" FROM "AccountType" WHERE "TypeName" = 'Current Liabilities'),
       true, 0, 'PKR', true
WHERE NOT EXISTS (SELECT 1 FROM "Account" WHERE "AccountCode" = '2170');

COMMIT;
