/* ═══════════════════════════════════════════════════════════════════════════
   18 — THE SALESPERSON'S OWN BOOK, THE RETURN NOTE, AND WHICH PLACE
        SOMEBODY WORKS AT
   ═══════════════════════════════════════════════════════════════════════════

   Five separate things, in one file because they were asked for together and
   they have to land together — three of them are useless without the others.

     1. A salesperson may see an invoice and raise a return.
     2. Whether Cloudinary will actually SERVE a stored bill.
     3. Who opened a customer account, so a rep's list can be their own.
     4. A warehouse keeper or an order-desk clerk belongs to ONE place.
     5. A sales return is a document, and documents get a number.

   Safe to run twice. Every statement is guarded.
   ═══════════════════════════════════════════════════════════════════════════ */


/* ═══════════════════════════════════════════════════════════════════════════
   1.  THE SALES ROLE COULD NOT SEE A BILL IT HAD JUST RAISED
   ═══════════════════════════════════════════════════════════════════════════

   The Sales role held five permissions: orders.view, orders.create,
   customers.view, customers.manage, reports.view. It did NOT hold
   invoices.view.

   Everything followed from that one missing row:

     · /sales/invoices bounced the rep to /forbidden, because src/proxy.ts
       opens that screen on the permission.
     · GET /sales/invoices answered 403, so "Print bill" on their own order
       had nothing to print.
     · The NEW SALES RETURN screen loads the invoice list and the refund
       methods in one Promise.all. The invoice half answered 403, the whole
       call rejected, and the screen said

           "Could not load invoices and refund methods."

       — which is why sales returns appeared to be broken. They were not
       broken. The rep was not allowed to look up the invoice being returned.

   invoices.create comes with it because the brief is explicit that a
   salesperson bills their own confirmed order — that is step 4 of the chain in
   Services/OrderWorkflow.cs, and the workflow has always allowed the move. The
   endpoint asked for a permission nobody had granted.

   NOT granted, deliberately: orders.approve. Confirming an order is the
   owner's decision and stays that way.
   ═══════════════════════════════════════════════════════════════════════════ */

INSERT INTO "RolePermission" ("RoleId", "PermissionId")
SELECT r."RoleId", p."PermissionId"
FROM "Role" r
CROSS JOIN "Permission" p
WHERE r."RoleKey" = 'sales'
  AND p."PermissionKey" IN ('invoices.view', 'invoices.create', 'returns.sales')
  AND NOT EXISTS (
      SELECT 1 FROM "RolePermission" rp
      WHERE rp."RoleId" = r."RoleId" AND rp."PermissionId" = p."PermissionId"
  );


/* ═══════════════════════════════════════════════════════════════════════════
   2.  WHETHER THE STORED BILL CAN ACTUALLY BE OPENED
   ═══════════════════════════════════════════════════════════════════════════

   This is the reason NOBODY — not the owner, not the warehouse, not the order
   desk — could open an invoice as a PDF.

   Every bill is rendered and pushed to Cloudinary, and the delivery URL that
   comes back is written to "SalesInvoice"."PdfUrl". The screen then opens that
   URL directly, because window.open cannot send an Authorization header.

   Cloudinary BLOCKS PDF DELIVERY BY DEFAULT on accounts created since 2023.
   The upload succeeds. A perfectly ordinary-looking secure_url comes back. And
   every request to it answers 401.

   Documents/PdfStore.cs has always checked this. It HEADs the URL after
   uploading and reports Deliverable, but for a sale invoice the answer was
   used only to pick the WhatsApp link and then thrown away. There was nowhere
   to keep it, so the screen had no way to know whether the link it was about
   to open was a live one.

   WHAT THE ACCOUNT IS DOING TODAY. Checked, not assumed, against a real stored
   bill on 17 September 2026:

       HEAD .../raw/upload/.../invoices/INV-26-8887_u7qavn.pdf  ->  200 OK

   So PDF delivery IS switched on now, and the Cloudinary link is the better
   one to hand out. That has not always been true and may not stay true. It is
   an account setting somebody can untick, which is exactly why the answer now
   belongs in a column instead of in an assumption.

   DEFAULT FALSE is deliberate even so. It is the honest starting point for
   rows that predate the column, and it is never left wrong for long:
   SalesController.EnsureBill spends ONE HEAD request on a bill whose flag is
   false, writes down what it finds, and never asks again. So the first person
   to open each old bill flips it to true, and every view after that goes
   straight to Cloudinary.

   FALSE therefore costs nothing but a slower first open: the API serves the
   same bytes itself, at a signed /api/sales/bill/... link that needs no
   account. If the flag is ever stuck false across the board, the setting to
   look at is
       Cloudinary console -> Settings -> Security
         -> Restricted media types -> allow PDF
   ═══════════════════════════════════════════════════════════════════════════ */

ALTER TABLE "SalesInvoice"
    ADD COLUMN IF NOT EXISTS "PdfDeliverable" BOOLEAN NOT NULL DEFAULT FALSE;


/* ═══════════════════════════════════════════════════════════════════════════
   3.  WHO OPENED THE CUSTOMER ACCOUNT
   ═══════════════════════════════════════════════════════════════════════════

   "a sales person will see only his data — only his created customers".

   "Party" could not answer that. It carries "SalesPersonUserId", which is the
   rep who OWNS the relationship — set by whoever filled the form in, left
   empty more often than not, and freely reassigned when a territory changes.
   That is a different fact from who typed the account in, and using one for
   the other means a rep loses sight of their own customer the day the owner
   moves the account to somebody else.

   So: a separate column, written once at creation and never changed.

   BACKFILL. Existing rows are given the rep they are assigned to, which is the
   closest true answer available — nothing recorded the creator before today.
   Rows with no rep stay NULL and are visible to the back office only, which is
   correct: nobody can claim them.

   The list a rep sees is "created by me OR assigned to me", so a customer
   handed over to a rep still appears for them. The PICKER on the order form is
   deliberately NOT filtered — the brief says a rep can still sell to anybody.
   ═══════════════════════════════════════════════════════════════════════════ */

ALTER TABLE "Party"
    ADD COLUMN IF NOT EXISTS "CreatedByUserId" INT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_party_created_by'
    ) THEN
        ALTER TABLE "Party"
            ADD CONSTRAINT fk_party_created_by
            FOREIGN KEY ("CreatedByUserId") REFERENCES "User" ("UserId")
            ON UPDATE CASCADE ON DELETE SET NULL;
    END IF;
END $$;

/* Only where nothing is recorded yet, so re-running never overwrites a real
   creator with the assigned rep. */
UPDATE "Party"
   SET "CreatedByUserId" = "SalesPersonUserId"
 WHERE "CreatedByUserId" IS NULL
   AND "SalesPersonUserId" IS NOT NULL;

CREATE INDEX IF NOT EXISTS "Party_CreatedByUserId_idx"
    ON "Party" ("CreatedByUserId");


/* ═══════════════════════════════════════════════════════════════════════════
   4.  A KEEPER BELONGS TO ONE WAREHOUSE, A CLERK TO ONE ORDER DESK
   ═══════════════════════════════════════════════════════════════════════════

   "if warehouse account of muhammadzain will be created then it will be asked
    which warehouse — either Lahore-Warehouse or Karachi-Warehouse".

   NO NEW TABLE. The schema already models this twice over and neither side was
   being used:

       "User"."PrimaryLocationId"   the one place this person works at
       "UserLocation"               every place they may work out of

   What was missing was the RULE. /admin/users would happily create a warehouse
   keeper with no location at all, or with three, or with the Claim Stock
   shelf — and the warehouse queue then showed them every order in the company
   regardless of which city it belonged to.

   The rule now lives in AdminUsersController.ValidateUser and in the form:
   exactly one location, and it must be of the right KIND — a warehouse for a
   keeper, a department for the order desk. Both kinds already exist in
   "LocationKind" (1 = warehouse, 3 = department), and the Super Admin creates
   as many of each as there are cities at /admin/locations.

   THE SEED ONLY EVER HAD KARACHI. The two rows below give Lahore its pair, so
   the dropdown has something to show the first time somebody opens it and the
   rule can be seen working. They are ORDINARY ROWS — rename them, deactivate
   them, or add Faisalabad beside them from the Locations screen. Nothing in
   the code knows their ids.

   Skipped entirely if a location of that kind already exists in that city, so
   this does not fight with warehouses added by hand.
   ═══════════════════════════════════════════════════════════════════════════ */

INSERT INTO "Location" ("LocationCode", "LocationName", "KindId", "CityId",
                        "AddressLine", "IsActive", "IsDefault", "ExcludeFromSellable")
SELECT 'LOC-06', 'Lahore Warehouse', 1, c."CityId", 'Hall Road, Lahore', TRUE, FALSE, FALSE
FROM "City" c
/* ILIKE, because the towns in this database are named "Lahore - Pakistan"
   rather than "Lahore" -- the country is part of the string, since the city
   list also carries three hundred Chinese ones for the supplier forms. An
   equality test here matched nothing and the insert silently did nothing. */
WHERE c."CityName" ILIKE 'Lahore%'
  AND NOT EXISTS (
      SELECT 1 FROM "Location" l
      WHERE l."CityId" = c."CityId" AND l."KindId" = 1
  )
  AND NOT EXISTS (SELECT 1 FROM "Location" WHERE "LocationCode" = 'LOC-06')
LIMIT 1;

INSERT INTO "Location" ("LocationCode", "LocationName", "KindId", "CityId",
                        "AddressLine", "IsActive", "IsDefault", "ExcludeFromSellable")
SELECT 'LOC-07', 'Lahore Order Department', 3, c."CityId", 'Hall Road, Lahore', TRUE, FALSE, FALSE
FROM "City" c
/* ILIKE, because the towns in this database are named "Lahore - Pakistan"
   rather than "Lahore" -- the country is part of the string, since the city
   list also carries three hundred Chinese ones for the supplier forms. An
   equality test here matched nothing and the insert silently did nothing. */
WHERE c."CityName" ILIKE 'Lahore%'
  AND NOT EXISTS (
      SELECT 1 FROM "Location" l
      WHERE l."CityId" = c."CityId" AND l."KindId" = 3
  )
  AND NOT EXISTS (SELECT 1 FROM "Location" WHERE "LocationCode" = 'LOC-07')
LIMIT 1;

/* The Karachi pair is named "Warehouse" and "Order Department" — true when
   there was only one city and confusing the moment there are two. Renamed only
   if nobody has already renamed them. */
UPDATE "Location" SET "LocationName" = 'Karachi Warehouse'
 WHERE "LocationCode" = 'LOC-01' AND "LocationName" = 'Warehouse';

UPDATE "Location" SET "LocationName" = 'Karachi Order Department'
 WHERE "LocationCode" = 'LOC-02' AND "LocationName" = 'Order Department';

/* Anybody already holding one of those two roles with exactly one location and
   no primary set gets it filled in, so existing accounts are not left in the
   state the new rule forbids. */
UPDATE "User" u
   SET "PrimaryLocationId" = (
       SELECT ul."LocationId" FROM "UserLocation" ul
        WHERE ul."UserId" = u."UserId" LIMIT 1)
 WHERE u."PrimaryLocationId" IS NULL
   AND u."RoleId" IN (SELECT "RoleId" FROM "Role"
                       WHERE "RoleKey" IN ('warehouse-keeper', 'order-dept'))
   AND (SELECT COUNT(*) FROM "UserLocation" ul WHERE ul."UserId" = u."UserId") = 1;


/* ═══════════════════════════════════════════════════════════════════════════
   5.  A SALES RETURN IS A DOCUMENT
   ═══════════════════════════════════════════════════════════════════════════

   "no need to make another invoice of that order, however sales return invoice
    will be generated".

   Exactly so, and the two halves are enforced in different places:

     · An order that already has an invoice does not get a second one. That
       guard is in SalesController — the invoice number is printed on paper a
       customer is holding, and a second bill for the same goods is how the
       same sale gets paid for twice.

     · A return gets its OWN printable document — a credit note carrying the
       SR number, the invoice it came off, the lines coming back and their
       condition. It goes through the same renderer and the same Cloudinary
       store as every other document in the system, so nothing new is needed
       here except the row that says what to call it.

   "DocumentSeries" already has 'sales.return' with prefix SR, which is what
   numbers the return itself. This adds nothing to it — the credit note carries
   the return's number rather than a number of its own, because two numbers for
   one event is one number too many.

   Nothing to migrate: "DocumentFile" is keyed on (DocKind, DocKey) and takes
   a new kind without a schema change. Listed here so the document store screen
   at /admin/documents has a name for the rows that are about to appear.
   ═══════════════════════════════════════════════════════════════════════════ */

-- (no DDL required for section 5)


/* ═══════════════════════════════════════════════════════════════════════════
   WHAT YOU SHOULD SEE AFTERWARDS
   ═══════════════════════════════════════════════════════════════════════════

   SELECT p."PermissionKey"
     FROM "RolePermission" rp
     JOIN "Role" r       ON r."RoleId" = rp."RoleId"
     JOIN "Permission" p ON p."PermissionId" = rp."PermissionId"
    WHERE r."RoleKey" = 'sales'
    ORDER BY 1;
   -- invoices.create, invoices.view, returns.sales must be in that list.

   SELECT "LocationCode", "LocationName", "KindId", "CityId"
     FROM "Location" ORDER BY "LocationId";
   -- Karachi Warehouse / Karachi Order Department / Lahore Warehouse /
   -- Lahore Order Department.

   SELECT COUNT(*) FILTER (WHERE "CreatedByUserId" IS NOT NULL) AS claimed,
          COUNT(*)                                              AS parties
     FROM "Party";


   ─────────────────────────────────────────────────────────────────────────────
   AND ONE THING THIS FILE DELIBERATELY DOES NOT FIX
   ─────────────────────────────────────────────────────────────────────────────

   Six orders have moved PAST the Invoiced step with no invoice behind them.
   They are the damage the status bug did before it was found: pressing the
   Invoiced step wrote a status and nothing else, so the order says it is
   billed and there is no "SalesInvoice" row to print.

       ORD-26-0140  Dispatched      ORD-26-0168  Dispatched
       ORD-26-0141  Dispatched      ORD-26-0170  Invoiced
       ORD-26-0143  To Order Dept   ORD-26-0171  Delivered

   They are NOT invoiced by this migration. Issuing six invoice numbers by
   hand, dated today, against goods that shipped weeks ago is a decision about
   the books and not a data repair: the numbers come off a live series, they
   land in a fiscal period, and the dates would be wrong.

   Instead the ORDER SCREEN now offers "Raise invoice" on any order that has
   none, at whatever point of the chain it has reached. It used to appear only
   while the order was still Confirmed, which is why these six could never be
   rescued. Open each one and press it, and the invoice is cut properly,
   numbered properly, and its PDF stored.

   To see the list at any time:

     SELECT o."OrderNo", s."StatusKey"
       FROM "SalesOrder" o
       JOIN "OrderStatus" s ON s."StatusId" = o."StatusId"
       LEFT JOIN "SalesInvoice" i ON i."OrderId" = o."OrderId"
      WHERE i."InvoiceId" IS NULL
        AND s."StatusKey" IN ('INVOICED','SEEN_BY_WAREHOUSE','TO_ORDER_DEPT',
                              'AT_ORDER_DEPT','PACKAGING','DISPATCHED','DELIVERED')
      ORDER BY o."OrderNo";

   It should stay empty from now on.
   ═══════════════════════════════════════════════════════════════════════════ */
