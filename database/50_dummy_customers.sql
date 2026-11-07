-- ============================================================================
--  50  Three dummy customer accounts, for trying the account pickers
-- ============================================================================
--
--  The owner, 8 Nov: "kuch customers ka dummy accounts bana bhi dena database
--  ma". Three customers, named "(Dummy)" so nobody mistakes them for a real
--  shop, coded DUMMY-01 to DUMMY-03, Retailer, Karachi, with an opening
--  balance each so their ledgers are not empty. Opened exactly as the Customer
--  Ledgers quick form opens one (CustomerLedgerController.OpenAccount): a
--  "User" with the customer role and no login, and its "Party".
--
--  Remove them, once they have no postings:
--    DELETE FROM "Party" WHERE "PartyCode" LIKE 'DUMMY-%';
--    DELETE FROM "User" u WHERE u."RoleId" = 5 AND u."FullName" LIKE '%(Dummy)'
--      AND NOT EXISTS (SELECT 1 FROM "Party" p WHERE p."UserId" = u."UserId");
--
--  Idempotent: a code already there is skipped.
-- ============================================================================

BEGIN;

SELECT setval(pg_get_serial_sequence('"User"', 'UserId'),
              GREATEST((SELECT MAX("UserId") FROM "User"), 1));

WITH wanted(code, name, opening, credit_limit) AS (
    VALUES ('DUMMY-01', 'Al Noor Mobiles (Dummy)',   25000.00, 100000.00),
           ('DUMMY-02', 'City Phone Centre (Dummy)', 48000.00, 150000.00),
           ('DUMMY-03', 'Star Accessories (Dummy)',      0.00,  50000.00)
), missing AS (
    SELECT w.* FROM wanted w
     WHERE NOT EXISTS (SELECT 1 FROM "Party" p WHERE p."PartyCode" = w.code)
), users AS (
    INSERT INTO "User" ("RoleId", "RequiresEmail", "FullName", "IsActive", "CreatedAt", "MustChangePassword")
    SELECT (SELECT "RoleId" FROM "Role" WHERE "RoleKey" = 'customer'), false, m.name, true, CURRENT_DATE, false
      FROM missing m
    RETURNING "UserId", "FullName"
)
INSERT INTO "Party" ("UserId", "PartyCode", "LegalName", "CategoryId", "CityId", "CreditLimit",
                     "CreditDays", "HoldPolicyId", "OpeningBalance", "Rating")
SELECT u."UserId", m.code, m.name,
       (SELECT "CategoryId" FROM "PartyCategory" WHERE "CategoryKey" = 'RETAILER'),
       (SELECT "CityId" FROM "City" WHERE "CityName" LIKE 'Karachi%' ORDER BY "CityId" LIMIT 1),
       m.credit_limit, 30,
       (SELECT "PolicyId" FROM "CreditHoldPolicy" WHERE "PolicyKey" = 'WARN'),
       m.opening, 'C'
  FROM users u JOIN missing m ON m.name = u."FullName";

COMMIT;
