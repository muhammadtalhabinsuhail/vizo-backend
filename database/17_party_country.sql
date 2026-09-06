/* ═══════════════════════════════════════════════════════════════════════════
   17 — WHICH COUNTRY A PARTY BELONGS TO
   ═══════════════════════════════════════════════════════════════════════════

   Suppliers are not all Pakistani. The goods come from Shenzhen and Guangzhou
   as often as from Karachi, and a Chinese supplier does not have an NTN, an
   STRN or a CNIC — it has a Unified Social Credit Code, a VAT registration and
   a Resident ID card, and all three look nothing like ours.

   So the new-party screen now asks, for a supplier or a customer-and-supplier,
   whether the party is Pakistani or Chinese, and everything downstream of that
   question — placeholders, the three tax fields, the phone format — follows.

   ─────────────────────── WHY THIS IS ON "Province" ─────────────────────────

   The obvious move is a "Country" column on "Party". It would be the wrong one.

   A party already carries a city, a city already carries a province, and the
   province is what actually decides the country: Guangdong is in China whether
   or not anybody remembered to tick a box, and Sindh is in Pakistan. Putting
   the country on the party as well creates a second answer to a question that
   already has one, and the day those two disagree — a Karachi address on a row
   flagged CN — nobody can tell which is right.

   One column, on the table that already knows. Everything else derives from it.

   The Chinese provinces and their cities were already in this database before
   this migration; all that was missing was anything saying so.

   Safe to run twice.
   ═══════════════════════════════════════════════════════════════════════════ */


/* ─────────────────────────── the column ─────────────────────────────────── */

/* Two letters, ISO 3166-1 alpha-2. Defaulting to PK is the honest default:
   this is a Pakistani business, everything already in the table that is not
   named below is Pakistani, and a province added later without a thought is far
   more likely to be another one of ours. */
ALTER TABLE "Province"
    ADD COLUMN IF NOT EXISTS "Country" CHAR(2) NOT NULL DEFAULT 'PK';


/* ─────────────────────────── the Chinese ones ──────────────────────────── */

/* Listed by name rather than by id range. The ids happen to be contiguous
   today, which is exactly the kind of accident that stops being true the first
   time somebody inserts a province in the middle. */
UPDATE "Province" SET "Country" = 'CN'
WHERE "ProvinceName" IN (
    -- provinces
    'Anhui', 'Fujian', 'Gansu', 'Guangdong', 'Guizhou', 'Hainan', 'Hebei',
    'Heilongjiang', 'Henan', 'Hubei', 'Hunan', 'Jiangsu', 'Jiangxi', 'Jilin',
    'Liaoning', 'Qinghai', 'Shaanxi', 'Shandong', 'Shanxi', 'Sichuan',
    'Yunnan', 'Zhejiang', 'Taiwan',
    -- municipalities
    'Beijing', 'Chongqing', 'Shanghai', 'Tianjin',
    -- autonomous regions
    'Guangxi', 'Inner Mongolia', 'Ningxia', 'Tibet', 'Xinjiang',
    -- special administrative regions
    'Hong Kong', 'Macau'
);


/* ─────────────────── the tax columns already fit ───────────────────────── */

/*  No widening is needed, and none is done. What the Chinese formats need:

      Unified Social Credit Code   18 characters   "Ntn"   is VARCHAR(20)  ✓
      VAT / taxpayer registration  15 or 18        "Strn"  is VARCHAR(30)  ✓
      Resident ID card             18 characters   "Cnic"  is VARCHAR(20)  ✓

    and there is no CHECK constraint on any of the three, so the database has
    always been willing to store them. What refused them was the FORM, whose
    regexes only ever described the Pakistani shapes. That is where the fix
    belongs, and PartiesController.ValidateParty now enforces the right one of
    the two on the way in -- picked from the party's own city.                */


/* ─────────────────────────── what you should see ────────────────────────── */
--  SELECT "Country", COUNT(*) FROM "Province" GROUP BY 1 ORDER BY 1;
--    CN | 34
--    PK |  7
--
--  SELECT c."CityName", p."ProvinceName", p."Country"
--    FROM "City" c JOIN "Province" p ON p."ProvinceId" = c."ProvinceId"
--   WHERE p."Country" = 'CN' ORDER BY 1 LIMIT 5;
