/* ═══════════════════════════════════════════════════════════════════════════
   33 — THE ORDER DESK SEES NO MONEY, AND CAN OPEN CUSTOMERS AND TAKE ORDERS
   ═══════════════════════════════════════════════════════════════════════════

   Applied to the local test copy only — NOT yet run on live.
   Idempotent: DELETE/INSERT guarded by existence.

   The owner, 26 September, about the Order Department only:

     "They must have no money, accounts or purchases anywhere: no collections,
      vouchers, ledgers, customer balances or credit amounts, and no purchases
      or cost prices."

   Three layers say that. This file is the permission layer; the API refuses
   the role by name on every money endpoint (see NOTES-b.md for the list), and
   the menu follows the permissions below on the next sign-in.

   ─────────────────────────── WHAT IS TAKEN AWAY ─────────────────────────────

   cost.view     "See cost price". Nothing but the grant itself said the order
                 desk could; it is a flat contradiction of the rule above.
   reports.view  opens the Insights section, whose headline screen is the
                 Sales Summary -- revenue, by day and by place. That is money.
                 Slow Selling and Not Selling are STOCK screens and hang off
                 stock.view, which the desk keeps, so they stay in its menu.

   ─────────────────────────── WHAT IS KEPT OR GIVEN ──────────────────────────

   customers.view / customers.manage  already held: the desk opens customers.
   orders.create / orders.view        already held: the desk takes orders for
                                      ANY customer now, picking the salesperson
                                      each one belongs to (SalesController).
   stock.view / stock.transfer / stock.correct  kept, as the owner said.

   The Accountant is checked, not changed: it already holds customers.manage,
   so Super Admin and Accountant can both open customer accounts (point 4).

   Everybody signed in keeps their old permissions until their token expires
   (8 hours) -- sign out and back in after this runs.

   ═══════════════════════════════════════════════════════════════════════════ */

BEGIN;

DELETE FROM "RolePermission" rp
 USING "Role" r, "Permission" p
 WHERE rp."RoleId" = r."RoleId"
   AND rp."PermissionId" = p."PermissionId"
   AND r."RoleKey" = 'order-dept'
   AND p."PermissionKey" IN ('cost.view', 'reports.view', 'money.view', 'money.manage',
                             'ledger.view', 'ledger.manage', 'statements.view',
                             'purchases.view', 'purchases.manage', 'receipts.stock',
                             'limits.manage', 'expenses.manage');

/* The accountant opens customer accounts -- asserted, and granted if a
   hand edit in Setup > Roles ever took it away. */
INSERT INTO "RolePermission" ("RoleId", "PermissionId")
SELECT r."RoleId", p."PermissionId"
  FROM "Role" r, "Permission" p
 WHERE r."RoleKey" = 'accountant'
   AND p."PermissionKey" IN ('customers.view', 'customers.manage')
   AND NOT EXISTS (SELECT 1 FROM "RolePermission" x
                    WHERE x."RoleId" = r."RoleId" AND x."PermissionId" = p."PermissionId");

COMMIT;

/* ── rollback ────────────────────────────────────────────────────────────────
   INSERT INTO "RolePermission" ("RoleId","PermissionId")
   SELECT r."RoleId", p."PermissionId" FROM "Role" r, "Permission" p
    WHERE r."RoleKey"='order-dept' AND p."PermissionKey" IN ('cost.view','reports.view')
      AND NOT EXISTS (SELECT 1 FROM "RolePermission" x WHERE x."RoleId"=r."RoleId" AND x."PermissionId"=p."PermissionId");
*/
