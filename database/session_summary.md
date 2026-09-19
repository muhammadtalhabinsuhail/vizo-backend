# Session Summary — 31 Aug to 20 Sep 2026

A short record of what was built. Full reasoning, warnings and decisions are in
`convey.txt`; everything still to be done by hand is in `changa.txt`.

---

## 1. Accounting module — 31 Aug
- The 9 accounting screens now run on live database data instead of mock data.
- Accounting create / edit / delete added to the API; 4 ledger faults found and fixed.
- `db_ans.pdf` written (Roman Urdu): static-data audit, AI plan, Web Push map.

`web a892a9e` · `api ee7fb46`

## 2. Full Completion Order — 2 Sep
- Super Admin email changed to `vizo.com.pk@gmail.com` (password untouched).
- The 3 mock dashboards moved to live data; hard-coded cards and dead code removed.
- **AI (Gemini Flash):** SQL does the maths, AI only explains.
- **Web Push:** VAPID keys, service worker, 40 trigger points, per-user on/off.

`web 0ada0c1, 89ab2ac, 05ea62e` · `api 8031a95, efef3bd`

## 3. Credentials put back — 2 Sep
- All backend secrets returned to `appsettings.json` and frontend variables to
  `.env.local`; User Secrets removed.

## 4. Order workflow — 3 Sep
- **The chain (10 steps):** Draft → Submitted → Confirmed → Invoiced →
  Seen by Warehouse → On way to Order Dept → Received at Order Dept →
  Packaging → Dispatched → Delivered.
- New role **Warehouse Keeper** with a picking queue at `/warehouse`.
- A salesperson sees only their own orders, invoices and returns.
- Permission-based authorisation replaced hard-coded role lists.
- **Date bug fixed:** one Pakistan clock (`BusinessClock`).

`api 596fe27` · `web 889323b`

## 5. Notifications and warehouse steps — 3 Sep
- Every notification stores a link; every non-admin action notifies the admin by name.
- SignalR bell updates live.

`api a7626b9` · `web b98f8de`

## 6. Chinese parties — 6 Sep
- `/parties/new` asks **Pakistani or Chinese** for a supplier; tax fields become
  **USCC / VAT No. / ID Card**. Country is read from the city's province.
- `/parties/[id]/edit` built — it did not exist.

`api 30db380, 0c76373, 4ed116b` · `web 3205b15, ae4f165, 55c0204`

---

## 7. Invoices, sales returns, places and notifications — 17 Sep

### The three faults behind what was reported
| Symptom | What it actually was |
|---|---|
| "Clicking Invoiced makes no invoice" | `PATCH /orders/{id}/status` wrote a status id and nothing else. Six live orders had shipped with no invoice at all. |
| "Sales return: could not load invoices and refund methods" | The Sales role did not hold `invoices.view`. `GET /sales/invoices` answered 403 and took the whole `Promise.all` down with it. |
| "Nobody can see the invoice as a PDF" | Partly the above; partly that `PdfUrl` was handed out without ever checking Cloudinary would serve it. |

### What changed
- **Invoiced now invoices.** Both routes call one method. Idempotent: an order
  that already has an invoice never gets a second one, the step just makes sure
  the PDF exists. Cancelled and credit-held orders are refused.
- **The status moves forward only.** Billing a dispatched order no longer rewinds it.
- **`SalesInvoice.PdfDeliverable`** records whether Cloudinary will actually serve
  the stored bill, and re-checks a false answer exactly once. Every Print button
  now opens the link that works.
- **The sales return gets a credit note of its own** — `sales-return` is a document
  kind, archived to Cloudinary on creation, carrying the invoice, the order, each
  line's condition and the credit due. The order keeps its single invoice.
- **Sales returns on the owner's dashboard:** a column of its own, count in red,
  a link, and the five most recent by order number.
- **A salesperson's customers are their own** — new `Party.CreatedByUserId`. The
  order form's customer picker is deliberately unfiltered.
- **A warehouse keeper or order-desk clerk belongs to exactly one place**, of the
  right kind. The form asks "which warehouse"; the API enforces it. The picking
  queue is filtered to the keeper's own warehouse.
- **Stock in hand by city** at `/inventory/stock-levels` — whole system, or one
  city (its warehouse and order desk added together). Filtered server-side.
- **Notifications name their recipient** — "For Muhammad Zain (Warehouse Keeper)."
  — and carry a real link built from `App:WebBaseUrl` (env `App__WebBaseUrl`).
- Staff-only roles on the user form; `/admin/users` no longer offers Customer.

### Run against live data, on request
- `18_sales_scope_returns_and_places.sql` applied and verified.
- The six stranded orders invoiced through the real endpoint (INV-26-8888 to
  INV-26-8893), dated from their orders, PDFs on Cloudinary.
- Raising them briefly rewound five order statuses; all restored, each with an
  `ORDER_STATUS_RESTORED` row in the activity log. See `convey.txt` §7A.
- Every changed endpoint smoke-tested as three different people. All green.

---

## 8. Products — 20 Sep

- **SKU made, not typed:** `VZ-WORD-MODEL-CAT-COLOUR-NN`, e.g. *VIZO Titan T9 Wireless
  Earbuds - Black* in Earbuds → `VZ-TIT-T9-EAR-BLK-01`. Worked out on the server inside the
  insert's transaction, under a lock; the form shows a live preview. A barcode carrying one
  of our SKUs wins. Existing SKUs untouched. Rules in `Services/SkuGenerator.cs`.
- **The same product twice is refused** — same name ignoring case and spaces, on the form
  and on the server. A different colour or model goes in with its own SKU.
- **Brand defaults to VIZO** (looked up by name), still changeable.
- **Pricing:** Cost · Duty · Margin price · Margin % · Sale. Margin % is on cost + duty.
  New columns `DutyPrice`, `MarginPrice`. **Opening cost removed** everywhere; the column
  drop waits for the deploy (changa.txt §A1).
- **Barcode camera:** native reader first, `@zxing` fallback loaded on demand; 3-minute
  limit, clear messages for no camera / refused / busy / not https. Not tested with a real
  camera — the test browser has none.
- **/inventory/products as cards** with big images; status filter and counts fixed to cover
  the whole catalogue.
- **Movements as cards** (one per transfer), each opening its own page with both legs and
  **See complete transfer**.
- **History page** — timeline, stock ledger (opening + in − out = on hand), Excel export
  with seven tabs. Built for phones.
- **No dates before today** on the 19 forms where a date is entered; list/report filters
  left alone on purpose. Edit screens keep the record's own date.
- **Times on screen were five hours late** since 3 Sep (API writes Pakistan time, screens
  read UTC). Fixed in `lib/format.ts`.

### Found and not changed
- **Orders never take stock off the shelf** — 26 of 31 order invoices have no stock
  movement. Needs a decision on which step moves the stock. `convey.txt` §P0.
- **Seven exports silently stop at 50 rows** (orders is at 46). Fixed for products only.

---

## 9. Places, rights and returns — 21 Sep

- **"In Transit" is gone** — the location and the location type. It was holding 240 units
  that TRF-26-0014 had already delivered to Shop 2, so they were counted twice. One order
  and its invoice (ORD-26-0158 / INV-26-8878) were moved to Karachi Order Department first:
  every FK into `Location` cascades, so deleting the row would have deleted them.
- **Every customer dropdown is the rep's own list now** — same rule as the Customers screen
  (opened by me, or assigned to me), served from `/sales/lookups` and enforced again in
  `ValidateOrderRequest`. Reverses the 17 Sep decision, at the owner's word.
- **Billing moved to the back office.** Confirmed → **Invoiced/Edit** is the accountant's or
  the owner's; the Raise-invoice button, the endpoint and the "invoice it too" tick all ask
  the same rule (`OrderWorkflow.MayInvoice`). The accountant may also edit the order while it
  is confirmed or invoiced (`MayEditOrder`). Sales lost `invoices.create`.
- **Sales returns are admin + accounts only** — screen, sidebar, route guard, permission and
  role check. Sales and the order desk lost `returns.sales`.
- **The sales return is against the CUSTOMER, not one invoice.** `SalesReturn.InvoiceId` is
  nullable; the ceiling is bought − already returned per item, recomputed inside the writing
  transaction under a per-customer advisory lock. Price = what that customer actually paid on
  average (LineTotal ÷ qty). Date is today, one location for the whole return, condition
  derived from the shelf, POSTED on save with its credit note.
- **New screen** at `/sales/returns/new`: salesperson (optional) → customer → their last five
  orders and everything they have ever bought on the right, tap to add → items with steppers
  → location chips → save. Built for a phone, with a sticky save bar.

### Found and not changed
- **A sale invoice still does not reach the ledger** (39 invoices, 12 entries, all seeded).
  Returns are consistent with it. `convey.txt` §R7.1.
- **Per-invoice "returned" counts stop growing** — new returns name no invoice. §R7.2.
- **Two reps have no customers assigned**, so they cannot raise an order until the owner
  assigns accounts. `changa.txt` §B1 — the most urgent item in this round.

`api` (local, not pushed) · `web` (local, not pushed)

---

## Database migrations — all run on live Neon
| File | Adds |
|---|---|
| `15_order_workflow.sql` | Order statuses, Warehouse Keeper role, change requests |
| `16_warehouse_and_notification_links.sql` | *Seen by Warehouse*, notification links |
| `17_party_country.sql` | `Province.Country` (PK / CN) |
| `18_sales_scope_returns_and_places.sql` | Sales permissions, `PdfDeliverable`, `Party.CreatedByUserId`, the Lahore pair |
| `19_product_pricing.sql` | `DutyPrice`, `MarginPrice` (run). Section 2 drops `OpeningCost` — **not run yet**, after deploy |
| `20_places_rights_and_returns.sql` | In Transit removed, billing and returns rights narrowed, `SalesReturn.InvoiceId` nullable, INVOICED renamed *Invoiced/Edit* |

Run these on any other environment (local copy, staging).

## Good to know
- **Everybody must sign out and back in** — permissions ride inside the JWT.
- Set `App__WebBaseUrl` on the API host. See `changa.txt` §1.3.
- Ten of 38 invoices still have no archived PDF. Nothing is broken; opening one
  archives it.
- **Reps with no assigned customers can no longer raise an order at all** — the picker is
  their own list now. Assign accounts at People → Customers. `changa.txt` §B1.
- `.env.example` still has a VAPID public key that does not match the server.
