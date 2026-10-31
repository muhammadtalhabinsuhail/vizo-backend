-- ============================================================================
--  46  Three Cash & Bank accounts: Cash on Hand, Meezan, Faysal
-- ============================================================================
--
--  The owner, 1 Nov: "Keep only Cash on Hand, Meezan Bank, and Faysal Bank.
--  Delete all the remaining bank and wallet related only accounts."
--
--  The accounts removed had postings, vouchers, expenses and reconciliations
--  on them, and three foreign keys onto "Account" cascade on delete -- a plain
--  DELETE would have taken those vouchers and expenses with it. So their
--  history is MOVED first, then the empty accounts are deleted:
--
--     1102  Cash - Shop 2         ->  1101  Cash on Hand
--     1110  HBL Bank Account      ->  1111  Meezan Bank Account
--     1112  UBL Bank Account      ->  1111  Meezan Bank Account
--     1120  Easypaisa Wallet1     ->  1111  Meezan Bank Account
--     1121  JazzCash Wallet       ->  1111  Meezan Bank Account
--
--  Every journal line, voucher, expense, collection and purchase-duty pointer
--  is re-pointed, and the removed account's opening balance is added to its
--  target's, so the trial balance and every total stay exactly as they were.
--  The bank reconciliations of HBL and UBL are statements of those accounts
--  and cannot be re-read as Meezan's, so they are deleted (with their lines).
--
--  The JazzCash and Easypaisa payment METHODS are switched off; their rows
--  stay because old collections point at them. The API maps a generic Bank or
--  Cheque to Meezan now (LedgerPosting.CashAccountCodeFor).
--
--  Rollback: none in SQL -- the merged postings cannot be told apart again.
--  Restore the backup taken before this ran.
--
--  Idempotent: once the five accounts are gone it does nothing.
-- ============================================================================

BEGIN;

CREATE TEMP TABLE acct_map ON COMMIT DROP AS
SELECT f."AccountId" AS from_id, t."AccountId" AS to_id
FROM (VALUES ('1102', '1101'), ('1110', '1111'), ('1112', '1111'), ('1120', '1111'), ('1121', '1111')) AS m(from_code, to_code)
JOIN "Account" f ON f."AccountCode" = m.from_code
JOIN "Account" t ON t."AccountCode" = m.to_code;

-- reconciliations of the removed banks
DELETE FROM "BankStatementLine"
 WHERE "ReconciliationId" IN (SELECT "ReconciliationId" FROM "BankReconciliation"
                               WHERE "AccountId" IN (SELECT from_id FROM acct_map));
DELETE FROM "BankReconciliation" WHERE "AccountId" IN (SELECT from_id FROM acct_map);

-- move the history
UPDATE "JournalEntryLine" l SET "AccountId" = m.to_id FROM acct_map m WHERE l."AccountId" = m.from_id;
UPDATE "Voucher" v SET "CashBankAccountId" = m.to_id FROM acct_map m WHERE v."CashBankAccountId" = m.from_id;
UPDATE "Expense" e SET "PaidFromAccountId" = m.to_id FROM acct_map m WHERE e."PaidFromAccountId" = m.from_id;
UPDATE "Expense" e SET "ExpenseAccountId" = m.to_id FROM acct_map m WHERE e."ExpenseAccountId" = m.from_id;
UPDATE "Collection" c SET "DepositAccountId" = m.to_id FROM acct_map m WHERE c."DepositAccountId" = m.from_id;
UPDATE "PurchaseOrderItem" p SET "DutyAccountId" = m.to_id FROM acct_map m WHERE p."DutyAccountId" = m.from_id;
UPDATE "Account" a SET "ParentAccountId" = m.to_id FROM acct_map m WHERE a."ParentAccountId" = m.from_id;

-- carry the opening balances
UPDATE "Account" t
   SET "OpeningBalance" = t."OpeningBalance" + s.total
  FROM (SELECT m.to_id, SUM(f."OpeningBalance") AS total
          FROM acct_map m JOIN "Account" f ON f."AccountId" = m.from_id
         GROUP BY m.to_id) s
 WHERE t."AccountId" = s.to_id;

-- the empty accounts
DELETE FROM "Account" WHERE "AccountId" IN (SELECT from_id FROM acct_map);

-- the wallet methods
UPDATE "PaymentMethod" SET "IsActive" = false WHERE "MethodKey" IN ('JAZZCASH', 'EASYPAISA');

COMMIT;
