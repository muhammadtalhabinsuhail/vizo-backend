# AdvPOS — Super Admin API contract

Base URL comes from `.env.local`:

```ts
import { API_BASE_URL, authHeader } from "@/components/providers/session-provider";
// API_BASE_URL === process.env.NEXT_PUBLIC_API_BASE_URL  (http://localhost:5185/api)
```

Every `/admin/*` endpoint requires `Authorization: Bearer <jwt>` **and** the
`super-admin` role. `authHeader()` builds the header from the cookie.

Standard call shape used inside page components (no `api.ts` anywhere):

```tsx
const [rows, setRows] = React.useState<Row[]>([]);
const [loading, setLoading] = React.useState(true);
const [error, setError] = React.useState<string | null>(null);

const load = React.useCallback(async () => {
  setLoading(true);
  setError(null);
  try {
    const res = await axios.get<Row[]>(`${API_BASE_URL}/admin/couriers`, { headers: authHeader() });
    setRows(res.data);
  } catch (e) {
    setError(axios.isAxiosError(e) && e.response
      ? (e.response.data as { message?: string })?.message ?? "Could not load."
      : "Cannot reach the server.");
  } finally {
    setLoading(false);
  }
}, []);

React.useEffect(() => { void load(); }, [load]);
```

Errors always come back as `{ "message": "..." }` with 400/401/403/404.
Mutations return `{ "message": "..." }` and creates also return `{ "id": n }`.

---

## Dashboard

`GET /admin/dashboard`

```jsonc
{
  "businessDate": "2026-08-15",              // date the figures below cover
  "todaySales":   { "value": 7400.00, "orders": 0 },
  "collections":  { "value": 0.00 },
  "arOutstanding":{ "value": 18686050.00, "overdue60Plus": 0.00 },
  "apPayable":    { "value": 8940000.00, "dueIn7Days": 0.00 },
  "limitCrossed": [
    { "id": 12, "orderNo": "ORD-26-0143", "customerName": "Mobile Zone Lahore",
      "customerInitials": "ML", "salesPerson": "Imran Iqbal", "total": 96000.00,
      "creditHoldReason": "…", "creditLimit": 200000.00 }
  ],
  "claimsStuck":         { "count": 6, "value": 26140.00 },
  "deadStockValue":      0.00,
  "awaitingCollections": { "count": 2, "value": 200000.00 },
  "activity": [ { "id": 1, "user": "Umer Memon", "action": "UPDATED", "target": "…",
                  "detail": "…", "time": "2026-08-22T14:10:00", "location": "Warehouse",
                  "severity": "warning" } ],
  "salesTrend": [ { "date": "2026-08-03", "revenue": 142000.00 } ]
}
```

`POST /admin/orders/{id}/approve-credit-hold`  body `{ "reason": "optional" }`
`POST /admin/orders/{id}/hold`                 body `{ "reason": "min 5 chars" }`

---

## Users

`GET /admin/users?q=&page=1&pageSize=15&isActive=`

```jsonc
{ "items": [ { "id": 1, "fullName": "Umer Memon", "initials": "UM",
               "email": "vizo.com.pk@gmail.com", "phone": "0300 7287607",
               "employeeCode": "EMP-001", "roleId": 1, "roles": ["Super Admin"],
               "locations": ["LOC-01","LOC-02","LOC-03"],
               "isActive": true, "isLocked": false,
               "lastLoginAt": "2026-08-22T09:58:00", "createdAt": "2025-08-01" } ],
  "total": 11, "page": 1, "pageSize": 15 }
```

`GET  /admin/users/stats` → `{ "total": 11, "active": 10, "locked": 1 }`

`GET  /admin/users/{id}` → same fields plus
`roleKey`, `permissionCount`, `primaryLocationId`, and
`locations: [{ "locationId": 1, "locationCode": "LOC-01", "locationName": "Warehouse" }]`

`GET  /admin/users/{id}/activity?take=20` →
`[{ "id", "action", "entity", "detail", "ip", "time", "severity" }]`

`POST /admin/users` and `PUT /admin/users/{id}` body:

```jsonc
{ "fullName": "Hassan Raza", "email": "x@vizo.com.pk", "phone": "03001234567",
  "employeeCode": "EMP-013", "roleId": 4, "locationIds": [2,3],
  "isActive": true, "sendInvite": true, "password": null }
```

Server enforces: email + employee code unique, role must exist, email required.
`password: null` with `sendInvite` means a random password is set and the only
way in is the emailed reset code.

`PATCH  /admin/users/{id}/active` body `{ "value": true }`
`PATCH  /admin/users/{id}/lock`   body `{ "value": true }`
`POST   /admin/users/{id}/password-reset` (no body)
`DELETE /admin/users/{id}` body `{ "reason": "min 5 chars" }` — deactivates, never hard-deletes.

You cannot deactivate, lock or delete the account you are signed in with (400).

---

## Roles and permissions

`GET /admin/roles` →
`[{ "id", "key", "name", "description", "homePath", "isSystem", "isStaffRole", "userCount", "permissionCount" }]`

`GET /admin/roles/{id}` → same plus `permissions: string[]` (permission keys)

`GET /admin/permissions` →
`[{ "module": "Sales", "permissions": [{ "key": "orders.view", "label": "See customer orders" }] }]`

Eight modules: Sales (12), Purchases (4), Stock (5), Money (6), Delivery (2),
Claims (3), Reports (2), Administration (5) — 39 keys in total.
**This is the only permission catalogue. Delete the inline `PERMISSION_GROUPS`
in the role editor.**

`POST /admin/roles` / `PUT /admin/roles/{id}` body
`{ "name", "description", "homePath", "permissions": string[] }`
System roles cannot be renamed but can be re-permissioned.

`DELETE /admin/roles/{id}` body `{ "reason": "min 5 chars" }` — refused for
system roles and for roles that still have users.

---

## Locations

`GET /admin/locations?includeInactive=true` →
```jsonc
[{ "id": 1, "code": "LOC-01", "name": "Warehouse", "kindId": 1, "kind": "warehouse",
   "kindLabel": "Warehouse", "cityId": 1, "city": "Karachi",
   "address": "Kohinoor Market, Saddar, Karachi", "inChargeUserId": 4,
   "inCharge": "Bilal Ahmed", "isActive": true, "isDefault": false,
   "excludeFromSellable": false, "stockUnits": 10804 }]
```

`POST /admin/locations` / `PUT /admin/locations/{id}` body
`{ "code", "name", "kindId", "cityId", "address", "inChargeUserId", "isActive", "isDefault", "excludeFromSellable" }`

Setting `isDefault: true` clears it everywhere else automatically.
`DELETE /admin/locations/{id}` refuses while stock sits there, or if it is the default.

`GET /admin/location-kinds` → `[{ "id", "key", "name" }]` (warehouse, shop, department, claim, transit)

---

## Couriers

`GET /admin/couriers` →
```jsonc
[{ "id", "name", "shortName", "contactPerson", "phone", "codSettlementDays",
   "bookingCharge", "codFeePercent", "trackingUrlTemplate", "isActive",
   "consignmentCount" }]
```

`POST /admin/couriers` / `PUT /admin/couriers/{id}` body — same fields minus `id`/`consignmentCount`.
`DELETE /admin/couriers/{id}` — retires (sets inactive) if deliveries reference it, otherwise deletes.

---

## Account types

`GET /admin/account-types?group=Assets|all` →
```jsonc
{ "items": [{ "id", "name", "groupId", "group", "prefix", "codeLength",
              "normalBalance": "debit"|"credit", "onBalanceSheet": true,
              "isSystem": true, "accountCount": 0, "nextCode": "ACR00001" }],
  "groupCounts": [{ "group": "Assets", "count": 6 }],
  "total": 14 }
```

`PUT /admin/account-types/{id}` body `{ "name", "prefix", "codeLength", "normalBalance" }`
`GET /admin/account-groups` → `[{ "id", "name", "onBalanceSheet" }]`

---

## Document numbering

`GET /admin/document-series` →
```jsonc
{ "items": [{ "id", "key", "label", "prefix", "includeYear", "padding", "nextNumber" }],
  "yearSuffix": 26 }
```

`PUT /admin/document-series` body is the **whole grid**:
`[{ "id", "prefix", "includeYear", "padding", "nextNumber" }]`

Server rejects: prefix > 6 chars, padding outside 2–8, nextNumber < 1, duplicate prefixes.
Preview format: `includeYear ? PREFIX-YY-0001 : PREFIX-0001` using `yearSuffix`.

---

## Audit log

`GET /admin/audit-log?q=&from=YYYY-MM-DD&to=YYYY-MM-DD&severity=&page=1&pageSize=25`
```jsonc
{ "items": [{ "id", "user", "action", "entityType", "entityReference", "entity",
              "detail", "time", "ip", "location", "severity" }],
  "total": 42, "page": 1, "pageSize": 25 }
```

`GET /admin/audit-log/stats` →
`{ "totalToday", "failedLogins", "permissionChanges", "recentLogins" }`

`GET /admin/audit-log/{id}` → one entry plus `userEmail`.
`GET /admin/severity-levels` → `[{ "id", "key", "name" }]` — info, success, warning, danger, muted.

---

## Backups

`GET  /admin/backups` →
`[{ "id", "startedAt", "type", "typeKey", "status", "statusKey", "sizeMb", "destination", "durationSeconds", "hash", "triggeredBy" }]`

`GET  /admin/backups/stats` →
`{ "lastBackupAt", "lastBackupStatus", "totalSizeMb", "retained", "successRate" }`

`POST /admin/backups/run` body `{ "typeKey": "MANUAL", "destination": "Manual download" }`
`GET  /admin/backup-types` → `[{ "id", "key", "name" }]`

Note: this records the run. Taking the actual dump is a `pg_dump` job, not a web request.

---

## Settings and company

`GET /admin/settings` → `[{ "id", "group", "key", "value", "description" }]`
22 rows across four groups: `stock`, `sales`, `delivery`, `claim`.

`PUT /admin/settings` body `[{ "key", "value" }]` — only the keys you send are touched.

`GET /admin/company` →
`{ "id", "companyName", "legalName", "addressLine", "cityId", "city", "country",
   "phone", "email", "ntn", "strn", "fiscalYearStartMonth", "currencyCode",
   "currencySymbol", "foreignRate" }`

`PUT /admin/company` body — same minus `id`/`city`/`foreignRate`.

---

## Lookups (one call fills every dropdown)

`GET /admin/lookups` →
```jsonc
{ "roles":         [{ "id", "key", "name", "description", "permissionCount" }],
  "locations":     [{ "id", "code", "name" }],
  "locationKinds": [{ "id", "key", "name" }],
  "cities":        [{ "id", "name", "province" }],
  "provinces":     [{ "id", "name" }],
  "staff":         [{ "id", "name", "role" }],
  "accountGroups": [{ "id", "name" }] }
```

---

## Uploads

`POST /upload/image?folder=products` — multipart, field name `file`.
Max 5 MB, JPG/PNG/WEBP/GIF, magic-byte checked. → `{ "url", "publicId", "width", "height", "format", "bytes" }`

`POST /upload/pdf?folder=invoices` — multipart, field name `file`.
Max 15 MB, PDF only, magic-byte checked. → `{ "url", "publicId", "bytes", "originalName" }`

Images and PDFs go to two **different** Cloudinary accounts, configured in
`appsettings.json` under `CloudinaryImages` and `CloudinaryPdfs`.

---

## Auth

`POST /auth/login` `{ email, password }` → `{ token, expiresAt, user }`
`GET  /auth/me` → the user object
`POST /auth/forgot-password` `{ email }` → always 200
`POST /auth/verify-code` `{ email, code }`
`POST /auth/reset-password` `{ email, code, newPassword }`
`POST /auth/change-password` `{ currentPassword, newPassword }` (authenticated)
`POST /auth/logout` (authenticated)

`user` shape:
```jsonc
{ "userId", "fullName", "email", "phone", "roleId",
  "role": "super-admin", "roleLabel": "Super Admin", "homePath": "/dashboard",
  "initials": "UM", "primaryLocationId", "employeeCode", "isActive",
  "permissions": ["orders.view", "..."] }
```

---

## Sales

Everything under `/sales/*` needs a signed-in member of staff. Some actions are
narrower — the policy is named against each one.

### Orders

`GET /sales/orders?q=&status=&customerId=&page=1&pageSize=50` →
`{ "total", "page", "pageSize", "items": [...] }`

`GET /sales/orders/{id}` → the header, the real lines, what has been paid,
where the delivery got to, the invoice if one exists, and the activity trail:
```jsonc
{ "id", "orderNo", "customerId", "customerName", "customerInitials",
  "customerCode", "customerPhone", "customerAltPhone", "customerAddress",
  "customerType", "city", "creditLimit", "creditDays", "holdPolicy",
  "locationId", "location", "salesPerson", "orderDate", "deliveryDate",
  "status", "statusName", "subtotal", "discount", "tax", "total",
  "methodId", "paymentMethod", "paymentMethodName",
  "creditHoldReason", "notes", "createdBy", "createdAt",
  "invoiceId", "invoiceNo", "invoicePdfUrl", "invoiceShareUrl",
  "paidAmount", "balance", "paymentStatus", "outstanding",
  "channel", "carrier", "trackingNo", "deliveryState", "dispatchedOn", "deliveredOn",
  "lines":    [{ "id", "lineNo", "productId", "name", "sku", "packing",
                 "qty", "rate", "discountPercent", "taxPercent", "lineTotal" }],
  "activity": [{ "id", "action", "entityType", "detail", "at", "severity", "user" }] }
```

`POST /sales/orders` body:
```jsonc
{ "customerId", "locationId", "salesPersonUserId": null,
  "orderDate": "2026-08-29", "deliveryDate": "2026-09-05", "dueDate": null,
  "methodId", "notes": null,
  "saveAsDraft": false,     // DRAFT: no credit check, no invoice
  "raiseInvoice": true,     // cut the invoice in the same transaction
  "lines": [{ "productId", "qty", "rate", "discountPercent", "taxPercent" }] }
```
→ `{ "id", "orderNo", "status", "onCreditHold", "invoiceId", "invoiceNo",
     "invoicePdfUrl", "invoiceShareUrl", "message" }`

An order over the customer's limit is saved `CREDIT_HOLD` and is **never**
invoiced, whatever `raiseInvoice` says. Line totals are recomputed server-side.
The document series is **`ORD`**, not `SO`.

`POST /sales/orders/{id}/invoice` (BackOffice) body `{ "methodId": null, "dueDate": null }`
→ `{ "invoiceId", "invoiceNo", "invoicePdfUrl", "invoiceShareUrl", "message" }`

`PATCH /sales/orders/{id}/status` body `{ "statusKey", "reason": null }`
→ `{ "id", "status", "statusName", "message" }`.
Refuses to cancel an order that has been invoiced. The reason goes on the
activity trail.

### Credit holds

`GET /sales/credit-holds` (Accountant) → the queue, each row carrying
`customerPhone`, `outstanding`, `paidAmount` and `overBy` so the screen can
remind, review or release without a second call.

`GET /sales/credit-holds/count` (any staff) → `{ "count" }` — the sidebar badge.

`POST /sales/credit-holds/{id}/override` (Accountant)
body `{ "reason", "raiseInvoice": true }` → `{ "id", "status", "statusName",
"invoiceId", "invoiceNo", "message" }`. The reason is required and is logged at
severity 3.

### Invoices

`GET /sales/invoices?q=&status=&customerId=&walkIn=false&page=1&pageSize=50`

`walkIn` is `false` (default, account invoices only), `true` (walk-in only) or
`all`. Walk-in counter bills are kept out of the ledger by default — they never
age and nobody chases them.

`GET /sales/invoices/{id}` → as the list row, plus `strn`, `isWalkIn`,
`shareUrl`, `notes`, `lines[].returnedQty`, and `company` — the letterhead
straight off the `Company` row:
```jsonc
"company": { "name", "legalName", "address", "city", "country",
             "phone", "email", "ntn", "strn", "currencyCode", "currencySymbol" }
```

`POST /sales/invoices` (BackOffice) — standalone or against an order.
→ `{ "id", "invoiceNo", "pdfUrl", "shareUrl", "total", "message" }`

`GET  /sales/invoices/{id}/pdf` → the bill as `application/pdf`, rendered from
the row on every request. This is what Print and Download open.

`POST /sales/invoices/{id}/pdf?force=false` → renders, uploads to the documents
Cloudinary account and stores the link. → `{ "pdfUrl", "shareUrl", "rebuilt", "message" }`

`GET /sales/bill/{invoiceNo}?k=<hmac>` — **anonymous**. The link the WhatsApp
share sends: a customer has no account here. `k` is an HMAC of the invoice
number under the JWT signing secret, compared in constant time; rotating that
secret revokes every link at once.

### Returns

`GET  /sales/returns?q=&status=`
`GET  /sales/returns/{id}` → header, lines with `soldQty`, the decision
(`decisionReason`, `decidedBy`, `decidedAt`) and the activity trail.

`POST /sales/returns` (BackOffice) body:
```jsonc
{ "invoiceId", "locationId", "returnDate", "reason", "refundMethodId",
  "lines": [{ "productId", "qty", "rate", "conditionId", "restockLocationId": null }] }
```
Refuses anything not on the invoice, and anything already returned. Resalable
lines go straight back on the shelf; the rest are written off.

`PATCH /sales/returns/{id}/status` (BackOffice)
body `{ "statusKey": "APPROVED" | "POSTED" | "REJECTED", "reason": null }`
→ `{ "id", "status", "statusName", "unitsReversed", "message" }`

**Rejecting takes the restocked units back off the shelf.** A reason is
required on reject.

### Counter sale

`POST /sales/direct` (OrderDept) body:
```jsonc
{ "customerId", "isWalkIn": true, "walkInName", "walkInPhone",
  "locationId", "methodId", "notes",
  "lines": [{ "productId", "qty", "rate", "discountPercent", "taxPercent" }] }
```
One call raises the order, the invoice, the stock movement and the bill.
→ `{ "orderId", "orderNo", "invoiceId", "invoiceNo", "isWalkIn",
     "customerName", "customerPhone", "subtotal", "discount", "tax", "total",
     "pdfUrl", "shareUrl", "message" }`

A walk-in is booked against the shared `VZ-C-WALKIN` party with the buyer's own
name and number on the invoice row; credit is refused. An existing shop gets an
ordinary invoice that shows in the ledger.

`GET /sales/direct/walkin?q=&from=&to=&page=1&pageSize=50` (OrderDept) →
`{ "total", "page", "pageSize", "totalValue", "items": [...] }`

### Lookups

`GET /sales/lookups?locationId=` — one call fills every picker on every sales form:
```jsonc
{ "orderStatuses", "invoiceStatuses", "returnStatuses", "paymentMethods",
  "conditions",
  "locations": [{ "id", "code", "name", "kind", "isSellable" }],
  "customers": [{ "id", "code", "name", "displayName", "city", "phone",
                  "creditLimit", "creditDays", "holdPolicy", "outstanding" }],
  "salesPeople",
  "products":  [{ "id", "sku", "name", "packing", "salePrice", "costPrice",
                  "taxRatePercent", "totalStock", "stockHere" }],
  "walkInCustomerId", "defaultTaxPercent", "company" }
```

`stockHere` is null unless `locationId` is passed. `isSellable` is false for
claim and in-transit stock — held, never sold. `defaultTaxPercent` is the rate
most of the catalogue carries, and is what the counter screen starts on.

---

## Documents — every PDF in the system

**Nothing is written to the API host's filesystem.** Every document is rendered
in memory from the database and pushed to the **`CloudinaryPdfs`** account in
`appsettings.json` (`advpos/documents/…`). `DocumentFile` records where each one
went; `10_document_files.sql` created it.

Two verbs on the same URL, and the difference matters:

| | |
|---|---|
| `GET`  | render from the database and stream the bytes — Print and Download |
| `POST` | render, upload to Cloudinary, record the link — Save to store |

### Getting a document to a person

`GET /documents/{kind}/{id}/download?attachment=false`

**302 to the document's own file in Cloudinary**, archiving it first if it has
never been archived. `attachment=true` adds Cloudinary's `fl_attachment` so the
browser saves rather than previews.

This route needs the bearer header like every other `/api` route, so it is for
API callers. A browser `window.open` sends cookies and NOT the header, so the
front end opens the Cloudinary URL directly instead — see
`vizo-erp/src/lib/documents.ts`.

Same for a sale invoice: `GET /sales/invoices/{id}/download?attachment=false`.

### Business documents

`GET  /documents/{kind}/{id}/pdf` → `application/pdf`
`POST /documents/{kind}/{id}/pdf?force=false` →
`{ "archived", "fileId", "kind", "docNo", "fileName", "pdfUrl", "bytes",
   "isDeliverable", "generatedAt", "shareUrl", "rebuilt", "message" }`

`GET /documents/{kind}/{id}/file` → the stored record, or `{ "archived": false }`.

`kind` is one of:

```
purchase-order   purchase-invoice   goods-receipt   purchase-return
stock-adjustment stock-transfer     voucher         journal-entry
expense          party-statement
```

For `party-statement` the id is the **party's UserId**.

### Reports

`GET  /reports/{key}/pdf?from=&to=&locationId=&asOf=&days=&minCoverDays=&limit=`
`POST /reports/{key}/pdf?…` — same query, archives it.

`key`: `sales-summary` · `aging-customer` · `aging-supplier` · `dead-stock` ·
`slow-moving` · `top-customers`

### Financial statements

`GET  /accounting/{key}/pdf?asOf=&from=&to=&accountId=`
`POST /accounting/{key}/pdf?…`

`key`: `trial-balance` · `balance-sheet` · `profit-loss` · `cash-flow` ·
`ledger` (needs `accountId`)

Reports and statements have no row to key a stored file off — a sales summary
for August is a document about a date range. The archive key is a fingerprint
of the parameters, so re-running the same report **replaces** its file instead
of piling up copies.

### The store

`GET /documents?kind=&q=&page=1&pageSize=50` (BackOffice) →
`{ "total", "page", "pageSize", "undeliverable", "items": [...] }`

`undeliverable` counts files Cloudinary accepted but will not serve. Surfaced on
`/admin/documents`.

`GET /documents/open/{kind}/{key}?k=<hmac>` — **anonymous**, same signing scheme
as `/sales/bill/{invoiceNo}`.

### Documents are archived when they are CREATED

Every create action pushes its PDF to Cloudinary before it returns —
`POST /purchases/orders`, `/purchases/grns`, `/purchases/invoices`,
`/purchases/returns`, `/inventory/adjustments`, `/inventory/transfers`,
`/accounting/journal-entries`, `/accounting/expenses`, `/accounting/vouchers`,
and every path in Sales that raises an invoice.

A failure there is logged and swallowed. By the time it runs the order is taken,
the stock has moved and the money is in the drawer; failing the request because
a document store was briefly unreachable would tell the operator the sale did not
happen and they would ring it up twice. The PDF can be rebuilt from the row at
any time — the sale cannot.

### `isDeliverable`

Every upload is followed by a HEAD on the URL it returned. Cloudinary blocks PDF
delivery by default on accounts created since 2023 — the upload succeeds and
every request to the link answers `401 deny or ACL failure`. When that is the
case the app hands out its own signed link instead of a broken one, and switches
back to Cloudinary automatically once the console setting is changed
(Settings → Security → Restricted media types).


---

## Spreadsheet export

Every list screen with an Export button returns a real `.xlsx` — one sheet,
frozen header, auto-filter, and money, dates and counts as **typed cells**
rather than text that merely looks like numbers.

| | |
|---|---|
| `GET /sales/orders/export` | `q`, `status`, `customerId` |
| `GET /sales/invoices/export` | `q`, `status`, `customerId`, `walkIn` |
| `GET /sales/returns/export` | `q`, `status` |
| `GET /sales/direct/walkin/export` | `q`, `from`, `to` |
| `GET /purchases/orders/export` | `q`, `status`, `supplierId` |
| `GET /parties/export` | `type`, `q`, `includeInactive` |
| `GET /inventory/products/export` | `q`, `categoryId`, `brandId`, `status`, `includeInactive` |

Each one runs the **same list action the screen runs** and writes its result, so
the file is what was on the page — filters and all — and the two cannot drift.

Behind `[Authorize]` like everything else, so the front end fetches the blob with
the header attached rather than navigating (`vizo-erp/src/lib/export.ts`).

The writer is `Documents/XlsxWriter.cs`: no NuGet package, an `.xlsx` being a zip
of six small XML parts and `System.IO.Compression` being in the framework. Same
reasoning as the PDF writer. One sheet only — if formulas or charts are ever
wanted, that is the moment to reach for a library rather than to grow that file.
