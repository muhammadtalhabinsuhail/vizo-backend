-- ============================================================================
--  42  The accountant approves orders; "new" badges on Orders and Customers
-- ============================================================================
--
--  Applied to the local test copy only -- NOT yet run on live.
--
--  The owner, 30 Sep:
--
--    * "Accountant tamam orders ko dekh sakta hai ... confirmed aur invoiced
--      dono kar sakta hai." The accountant (Hassan Raza) saw an EMPTY Orders
--      screen: the page decided "is this a rep?" by `!can("orders.approve")`,
--      and the accountant never held orders.approve -- so his list was
--      filtered to orders where he was the salesperson. The page now asks the
--      role instead, and OrderWorkflow lets the accountant confirm, decline,
--      cancel and hold. This grant makes the right match the rule, so the
--      order screen's back-office controls (credit-hold release, the status
--      dropdown) show for him too.
--
--    * "Orders ka bhi yahi scenario hona chahiye ... badge mein number ... jab
--      usko click karke khol liya jaye to wo badge hat jana chahiye." A count
--      of new orders and new customers next to Orders and Customers in the
--      sidebar, per person, cleared when that person opens the page. What each
--      person has already seen is one row per (user, area): the highest id
--      they had seen when they last opened it. Ids, not timestamps, because
--      "SalesOrder"."CreatedAt" and "User"."CreatedAt" are DATE columns -- two
--      orders on one day cannot be told apart by them, while the identity key
--      is the creation order exactly.
--
--    * The order edit screen shows, per line: quantity, ORIGINAL price, the
--      salesperson's MARGIN (money and %), the FINAL price and the total. An
--      order line only ever stored the final price (UnitPrice), so the original
--      had to be guessed from today's product price -- which moves every time a
--      purchase order re-prices the item. "SalesOrderItem"."BasePrice" keeps
--      the original per unit as it was when the line was written; the margin
--      is UnitPrice - BasePrice. NULL on every older line (the edit screen then
--      falls back to the product's current price, as before).
--
--  The API reads UserSeenMarker (SidebarBadgesController), so run this BEFORE
--  deploying the API. Additive and idempotent: safe to run twice.
--
--  Rollback:
--    DROP TABLE IF EXISTS "UserSeenMarker";
--    ALTER TABLE "SalesOrderItem" DROP COLUMN IF EXISTS "BasePrice";
--    DELETE FROM "RolePermission" rp USING "Role" r, "Permission" p
--     WHERE rp."RoleId" = r."RoleId" AND rp."PermissionId" = p."PermissionId"
--       AND r."RoleKey" = 'accountant' AND p."PermissionKey" = 'orders.approve';
-- ============================================================================

BEGIN;

INSERT INTO "RolePermission" ("RoleId", "PermissionId")
SELECT r."RoleId", p."PermissionId"
  FROM "Role" r, "Permission" p
 WHERE r."RoleKey" = 'accountant' AND p."PermissionKey" = 'orders.approve'
   AND NOT EXISTS (SELECT 1 FROM "RolePermission" x
                    WHERE x."RoleId" = r."RoleId" AND x."PermissionId" = p."PermissionId");

CREATE TABLE IF NOT EXISTS "UserSeenMarker" (
    "UserId"     integer     NOT NULL REFERENCES "User"("UserId") ON DELETE CASCADE,
    "Area"       varchar(40) NOT NULL,
    "LastSeenId" integer     NOT NULL DEFAULT 0,
    "SeenAt"     timestamp without time zone NOT NULL DEFAULT (now() AT TIME ZONE 'Asia/Karachi'),
    CONSTRAINT "UserSeenMarker_pkey" PRIMARY KEY ("UserId", "Area"),
    CONSTRAINT "UserSeenMarker_Area_check" CHECK ("Area" IN ('orders', 'customers'))
);

ALTER TABLE "SalesOrderItem" ADD COLUMN IF NOT EXISTS "BasePrice" numeric(14,2) NULL;

COMMIT;

-- Check: SELECT r."RoleKey", p."PermissionKey" FROM "RolePermission" rp
--          JOIN "Role" r USING ("RoleId") JOIN "Permission" p USING ("PermissionId")
--         WHERE p."PermissionKey" = 'orders.approve';
