-- 14_fiscal_periods_forward.sql
--
-- The calendar stopped at 31 Aug 2026 and the date is now September, so every
-- posting in the application was being refused:
--
--     "No fiscal period covers 2026-09-02, so EXP-26-0029 cannot be posted."
--
-- That message is correct -- an entry with no period to belong to has nowhere
-- to go, and refusing it is right. The fault is that nobody had opened the next
-- month. Approving an expense, posting a voucher and posting a journal entry
-- were all dead on the 1st of September, with an error that reads like a bug.
--
-- Periods run as plain calendar months here, not an Apr-Mar fiscal year: the
-- eight existing rows are Jan 2026 through Aug 2026, one per month, named
-- "MMM yyyy". This continues that to the end of 2027, open.
--
-- OPENING A PERIOD IS NOT THE SAME AS CLOSING ONE. These are all IsClosed =
-- FALSE. Closing is a decision an accountant makes when the month's books are
-- finished, and /accounting/period-close is where they make it.
--
-- Safe to run twice.

INSERT INTO "FiscalPeriod" ("PeriodName", "PeriodYear", "PeriodMonth", "StartDate", "EndDate", "IsClosed")
SELECT
    to_char(d, 'Mon YYYY'),
    EXTRACT(YEAR  FROM d)::int,
    EXTRACT(MONTH FROM d)::int,
    d::date,
    (d + INTERVAL '1 month - 1 day')::date,
    FALSE
FROM generate_series(
        DATE '2026-09-01',
        DATE '2027-12-01',
        INTERVAL '1 month') AS d
WHERE NOT EXISTS (
    SELECT 1 FROM "FiscalPeriod" p
    WHERE p."PeriodYear"  = EXTRACT(YEAR  FROM d)::int
      AND p."PeriodMonth" = EXTRACT(MONTH FROM d)::int
);

-- Verify:
--   SELECT "PeriodName","StartDate","EndDate","IsClosed"
--   FROM "FiscalPeriod" ORDER BY "StartDate";
--
-- KEEP THIS TOPPED UP. When the last row here runs out the same failure comes
-- back. The proper fix is for the period-close screen to open the next month
-- when it closes one; until then, run this again with later dates.
