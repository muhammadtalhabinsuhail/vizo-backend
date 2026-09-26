/* ═══════════════════════════════════════════════════════════════════════════
   30 — THE ACCOUNTS THE CUSTOMER AND STAFF LEDGERS POST TO
   ═══════════════════════════════════════════════════════════════════════════

   Applied to the local test copy only — NOT yet run on live.
   Idempotent: every insert is guarded, so running it twice changes nothing.

   The owner, 26 September: every customer and every member of staff gets a
   ledger built from what the system already records, and those ledgers POST
   TO THE MAIN BOOKS. Four things are missing from the chart for that.

   ───────────────────────── 1. FAYSAL BANK, 1113 ────────────────────────────

   "The old system's receipts mostly went to Faysal Bank." The chart has HBL
   (1110), Meezan (1111) and UBL (1112) and no Faysal -- so a receipt the
   business really banked at Faysal could only be filed under somebody else's
   bank. 1113 sits beside the other three, same type (Cash & Bank), same
   parent (Current Assets), opening at zero: whatever Faysal held before this
   system is an opening balance the owner types in at Account List, not a
   figure this file should invent.

   The receiving payment method for it already exists (migration 21 added
   "FAISAL", spelt that way); Services/LedgerPosting.cs maps it to 1113.

   ───────────────────── 2. STAFF PAYABLES, 2140 ────────────────────────────

   ONE control account with a per-staff subledger, not one account per person.

   The range B was given is 2140-2149 -- ten codes -- and the business already
   has more than ten people on the payroll once the drivers and helpers
   without a login are counted. An account per head would run out of numbers
   in the first month, and would put forty rows of payroll into the Account
   List and the trial balance, where the accountant does not want them.

   So 2140 is the ONE liability "what we owe our staff", and WHICH member of
   staff each line belongs to rides on the line itself -- "JournalEntryLine".
   "StaffId", added by migration 31, exactly the way "PartyUserId" already says
   which customer an Accounts Receivable line belongs to. The trial balance
   shows one figure; the staff ledger screen splits it by person. A credit
   balance is salary owed to them; a debit balance is an advance they owe
   back. 2141-2149 stay free on purpose.

   ─────────────────── 3. TWO JOURNAL ENTRY TYPES ────────────────────────────

   "JournalEntryType" has SALE, RECEIPT, PURCHASE, PAYMENT, EXPENSE, JOURNAL
   and TRANSFER. A sales return posted as SALE, or a salary posted as JOURNAL,
   would be filed under a heading that says something that is not true -- the
   same fault 2026-08-31 found when every hand-written entry was typed "Sale".
   SALES_RETURN and SALARY are added; the posting code falls back to JOURNAL
   if either is missing, so the order of deploy does not matter.

   ─────────────────── 4. SALES RETURNS, 4002, MUST EXIST ─────────────────────

   It does on live (and on this copy). Asserted here rather than assumed,
   because a sales return with nowhere to post would be refused at the moment
   the goods are already back on the shelf.

   ═══════════════════════════════════════════════════════════════════════════ */

BEGIN;

/* ── 1. Faysal Bank ─────────────────────────────────────────────────────── */
INSERT INTO "Account" ("AccountCode", "AccountName", "ParentAccountId", "AccountTypeId",
                       "IsGroup", "OpeningBalance", "CurrencyCode", "IsActive")
SELECT '1113', 'Faysal Bank Account',
       (SELECT "AccountId" FROM "Account" WHERE "AccountCode" = '1100'),
       (SELECT "AccountTypeId" FROM "AccountType" WHERE "TypeName" = 'Cash & Bank'),
       false, 0, 'PKR', true
WHERE NOT EXISTS (SELECT 1 FROM "Account" WHERE "AccountCode" = '1113');

/* ── 2. Staff Payables ──────────────────────────────────────────────────── */
INSERT INTO "Account" ("AccountCode", "AccountName", "ParentAccountId", "AccountTypeId",
                       "IsGroup", "OpeningBalance", "CurrencyCode", "IsActive")
SELECT '2140', 'Staff Payables',
       (SELECT "AccountId" FROM "Account" WHERE "AccountCode" = '2100'),
       (SELECT "AccountTypeId" FROM "AccountType" WHERE "TypeName" = 'Current Liabilities'),
       false, 0, 'PKR', true
WHERE NOT EXISTS (SELECT 1 FROM "Account" WHERE "AccountCode" = '2140');

/* ── 3. entry types ─────────────────────────────────────────────────────── */
INSERT INTO "JournalEntryType" ("TypeKey", "TypeName")
SELECT 'SALES_RETURN', 'Sales return'
WHERE NOT EXISTS (SELECT 1 FROM "JournalEntryType" WHERE "TypeKey" = 'SALES_RETURN');

INSERT INTO "JournalEntryType" ("TypeKey", "TypeName")
SELECT 'SALARY', 'Salary & staff'
WHERE NOT EXISTS (SELECT 1 FROM "JournalEntryType" WHERE "TypeKey" = 'SALARY');

/* ── 4. the accounts the sales posting needs, asserted ──────────────────── */
DO $$
DECLARE missing text;
BEGIN
    SELECT string_agg(code, ', ') INTO missing
      FROM unnest(ARRAY['1101','1110','1111','1113','1120','1121','1130','2110','2140','4001','4002','5101']) AS code
     WHERE NOT EXISTS (SELECT 1 FROM "Account" a WHERE a."AccountCode" = code AND NOT a."IsGroup");
    IF missing IS NOT NULL THEN
        RAISE EXCEPTION 'Accounts missing from the chart: %', missing;
    END IF;
END $$;

COMMIT;

/* ── rollback (only if nothing has posted to them yet) ──────────────────────
   DELETE FROM "Account" WHERE "AccountCode" IN ('1113','2140')
      AND NOT EXISTS (SELECT 1 FROM "JournalEntryLine" l WHERE l."AccountId" = "Account"."AccountId");
   DELETE FROM "JournalEntryType" WHERE "TypeKey" IN ('SALES_RETURN','SALARY')
      AND NOT EXISTS (SELECT 1 FROM "JournalEntry" e WHERE e."EntryTypeId" = "JournalEntryType"."EntryTypeId");
*/
