-- ═══════════════════════════════════════════════════════════════════════════
--  08_sales_documents.sql
--
--  Three things the sales screens needed and the schema did not have:
--
--    1. A place to keep the generated invoice PDF. The bill is rendered by the
--       API, pushed to the "CloudinaryPdfs" account and the returned link is
--       stored on the invoice row -- that link is what the WhatsApp share and
--       the Download button hand out, so it has to survive the request that
--       made it.
--
--    2. Walk-in identity. "SalesOrder"."CustomerUserId" is NOT NULL, so a
--       counter sale to somebody with no account still needs a party to hang
--       off. One shared "Walk-in Customer" party carries them all, and the
--       actual person's name and number live on the invoice row -- which is
--       also what /sales/direct/walkin lists.
--
--    3. Why a sales return was approved or rejected, and by whom. Rejecting is
--       a real decision with a real reason; without these columns the screen
--       could show a button but never record what it did.
--
--  Every statement is idempotent -- run it as many times as you like.
--
--  ROLLBACK is at the foot of the file.
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

-- ─────────────────────────── 1. invoice document ───────────────────────────

ALTER TABLE "SalesInvoice" ADD COLUMN IF NOT EXISTS "PdfUrl"      varchar(500);
ALTER TABLE "SalesInvoice" ADD COLUMN IF NOT EXISTS "PdfPublicId" varchar(255);

COMMENT ON COLUMN "SalesInvoice"."PdfUrl" IS
  'Cloudinary secure_url of the rendered bill. Null until the PDF is built.';
COMMENT ON COLUMN "SalesInvoice"."PdfPublicId" IS
  'Cloudinary public_id, kept so the asset can be replaced or deleted later.';

-- ──────────────────────────── 2. walk-in sales ─────────────────────────────

ALTER TABLE "SalesInvoice" ADD COLUMN IF NOT EXISTS "IsWalkIn"    boolean NOT NULL DEFAULT false;
ALTER TABLE "SalesInvoice" ADD COLUMN IF NOT EXISTS "WalkInName"  varchar(150);
ALTER TABLE "SalesInvoice" ADD COLUMN IF NOT EXISTS "WalkInPhone" varchar(30);

COMMENT ON COLUMN "SalesInvoice"."IsWalkIn" IS
  'True for a counter sale to somebody with no account. Those bills are listed
   at /sales/direct/walkin and are deliberately kept OUT of /sales/invoices,
   which is the shop-account ledger.';

CREATE INDEX IF NOT EXISTS "IX_SalesInvoice_IsWalkIn"
  ON "SalesInvoice" ("IsWalkIn", "InvoiceDate" DESC);

/* The shared walk-in party. Rating 'C' and a zero credit limit are deliberate:
   nothing may ever go on this account, and the BLOCK policy makes the credit
   check refuse it if anybody tries. */
DO $$
DECLARE
    walkin_id int;
BEGIN
    SELECT "UserId" INTO walkin_id FROM "Party" WHERE "PartyCode" = 'VZ-C-WALKIN';

    IF walkin_id IS NULL THEN
        INSERT INTO "User" ("RoleId", "RequiresEmail", "FullName", "Email", "Phone",
                            "PasswordHash", "PrimaryLocationId", "IsActive")
        VALUES (5, false, 'Walk-in Customer', NULL, NULL, NULL, NULL, true)
        RETURNING "UserId" INTO walkin_id;

        INSERT INTO "Party" ("UserId", "PartyCode", "LegalName", "DisplayName",
                             "CategoryId", "CityId", "AddressLine", "CreditLimit",
                             "CreditDays", "HoldPolicyId", "OpeningBalance",
                             "Rating", "Notes")
        VALUES (walkin_id, 'VZ-C-WALKIN', 'Walk-in Customer', 'Walk-in',
                1, 1, 'Counter sale', 0,
                0, 3, 0,
                'C', 'System account. Every cash counter sale with no shop account is booked here; the buyer''s own name and number are on the invoice row.');
    END IF;
END $$;

-- ───────────────────────── 3. sales return decision ────────────────────────

ALTER TABLE "SalesReturn" ADD COLUMN IF NOT EXISTS "DecisionReason" varchar(300);
ALTER TABLE "SalesReturn" ADD COLUMN IF NOT EXISTS "DecidedByUserId" int;
ALTER TABLE "SalesReturn" ADD COLUMN IF NOT EXISTS "DecidedAt" timestamp without time zone;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'FK_SalesReturn_DecidedBy'
    ) THEN
        ALTER TABLE "SalesReturn"
          ADD CONSTRAINT "FK_SalesReturn_DecidedBy"
          FOREIGN KEY ("DecidedByUserId") REFERENCES "User" ("UserId");
    END IF;
END $$;

COMMENT ON COLUMN "SalesReturn"."DecisionReason" IS
  'Why the return was approved, posted or rejected. Required on reject.';

COMMIT;

-- ═══════════════════════════════════════════════════════════════════════════
--  ROLLBACK
--
--    ALTER TABLE "SalesInvoice" DROP COLUMN IF EXISTS "PdfUrl";
--    ALTER TABLE "SalesInvoice" DROP COLUMN IF EXISTS "PdfPublicId";
--    ALTER TABLE "SalesInvoice" DROP COLUMN IF EXISTS "IsWalkIn";
--    ALTER TABLE "SalesInvoice" DROP COLUMN IF EXISTS "WalkInName";
--    ALTER TABLE "SalesInvoice" DROP COLUMN IF EXISTS "WalkInPhone";
--    DROP INDEX IF EXISTS "IX_SalesInvoice_IsWalkIn";
--    ALTER TABLE "SalesReturn" DROP CONSTRAINT IF EXISTS "FK_SalesReturn_DecidedBy";
--    ALTER TABLE "SalesReturn" DROP COLUMN IF EXISTS "DecisionReason";
--    ALTER TABLE "SalesReturn" DROP COLUMN IF EXISTS "DecidedByUserId";
--    ALTER TABLE "SalesReturn" DROP COLUMN IF EXISTS "DecidedAt";
--    -- the walk-in party is left alone: dropping it would orphan its invoices
-- ═══════════════════════════════════════════════════════════════════════════
