-- 11_series_expense_claim.sql
--
-- Two prefixes the C# asks for were never configured: EXP (Expense) and CLM
-- (Claim). NextNumber() does not throw on a missing series -- it falls back to
-- PREFIX-yyyyMMddHHmmss and carries on -- so nothing failed loudly. The rows
-- were simply numbered EXP-20260831165938 instead of EXP-26-0026, which no
-- human can read and no report can sort.
--
-- This is the same fault 09_document_series_catchup.sql fixed for ORD/GRN/PR/
-- ADJ, from the other direction: that one had a counter behind its data, this
-- one has no counter at all.
--
-- Safe to run twice. Everything below is guarded.

BEGIN;

-- 1. The two missing series, wound past whatever is already in the tables.
--    Padding 4 and IncludeYear to match every other series in the app.

INSERT INTO "DocumentSeries" ("SeriesKey", "Label", "Prefix", "IncludeYear", "Padding", "NextNumber")
SELECT 'expense.voucher', 'Expense Voucher', 'EXP', TRUE, 4,
       COALESCE((SELECT MAX(SUBSTRING("ExpenseNo" FROM '[0-9]+$')::int)
                 FROM "Expense" WHERE "ExpenseNo" ~ '^EXP-[0-9]{2}-[0-9]+$'), 0) + 1
WHERE NOT EXISTS (SELECT 1 FROM "DocumentSeries" WHERE "Prefix" = 'EXP');

INSERT INTO "DocumentSeries" ("SeriesKey", "Label", "Prefix", "IncludeYear", "Padding", "NextNumber")
SELECT 'claim.note', 'Claim', 'CLM', TRUE, 4,
       COALESCE((SELECT MAX(SUBSTRING("ClaimNo" FROM '[0-9]+$')::int)
                 FROM "Claim" WHERE "ClaimNo" ~ '^CLM-[0-9]{2}-[0-9]+$'), 0) + 1
WHERE NOT EXISTS (SELECT 1 FROM "DocumentSeries" WHERE "Prefix" = 'CLM');

-- 2. Rename the rows that already took a timestamp number.
--    They are the newest rows in both tables, so numbering them in ExpenseId /
--    ClaimId order keeps the sequence honest.

WITH bad AS (
    SELECT "ExpenseId", ROW_NUMBER() OVER (ORDER BY "ExpenseId") - 1 AS offs
    FROM "Expense"
    WHERE "ExpenseNo" !~ '^EXP-[0-9]{2}-[0-9]{1,}$'
), base AS (
    SELECT "NextNumber" AS n, "Padding" AS pad FROM "DocumentSeries" WHERE "Prefix" = 'EXP'
)
UPDATE "Expense" e
SET "ExpenseNo" = 'EXP-26-' || LPAD((base.n + bad.offs)::text, base.pad, '0')
FROM bad, base
WHERE e."ExpenseId" = bad."ExpenseId";

UPDATE "DocumentSeries" s
SET "NextNumber" = s."NextNumber" + (SELECT COUNT(*) FROM "Expense" WHERE "ExpenseNo" ~ '^EXP-26-[0-9]+$')
                 - (SELECT COUNT(*) FROM "Expense" WHERE "ExpenseNo" ~ '^EXP-26-[0-9]+$'
                    AND SUBSTRING("ExpenseNo" FROM '[0-9]+$')::int < s."NextNumber")
WHERE s."Prefix" = 'EXP';

WITH bad AS (
    SELECT "ClaimId", ROW_NUMBER() OVER (ORDER BY "ClaimId") - 1 AS offs
    FROM "Claim"
    WHERE "ClaimNo" !~ '^CLM-[0-9]{2}-[0-9]{1,}$'
), base AS (
    SELECT "NextNumber" AS n, "Padding" AS pad FROM "DocumentSeries" WHERE "Prefix" = 'CLM'
)
UPDATE "Claim" c
SET "ClaimNo" = 'CLM-26-' || LPAD((base.n + bad.offs)::text, base.pad, '0')
FROM bad, base
WHERE c."ClaimId" = bad."ClaimId";

UPDATE "DocumentSeries" s
SET "NextNumber" = s."NextNumber" + (SELECT COUNT(*) FROM "Claim" WHERE "ClaimNo" ~ '^CLM-26-[0-9]+$')
                 - (SELECT COUNT(*) FROM "Claim" WHERE "ClaimNo" ~ '^CLM-26-[0-9]+$'
                    AND SUBSTRING("ClaimNo" FROM '[0-9]+$')::int < s."NextNumber")
WHERE s."Prefix" = 'CLM';

-- 3. The archived PDF carries the old number in DocumentFile.DocNo. The file
--    itself is stale too, but it is rebuilt on the next save of that document;
--    this at least stops /admin/documents listing a number that no longer exists.

UPDATE "DocumentFile" f
SET "DocNo" = e."ExpenseNo"
FROM "Expense" e
WHERE f."DocKind" = 'expense' AND f."DocKey" = e."ExpenseId"::text AND f."DocNo" <> e."ExpenseNo";

UPDATE "DocumentFile" f
SET "DocNo" = c."ClaimNo"
FROM "Claim" c
WHERE f."DocKind" = 'claim' AND f."DocKey" = c."ClaimId"::text AND f."DocNo" <> c."ClaimNo";

COMMIT;

-- Verify:
--   SELECT "Prefix","NextNumber" FROM "DocumentSeries" WHERE "Prefix" IN ('EXP','CLM');
--   SELECT "ExpenseNo" FROM "Expense" ORDER BY "ExpenseId" DESC LIMIT 3;
