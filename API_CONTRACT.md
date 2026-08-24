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
               "email": "admin@advpos.pk", "phone": "0300 7287607",
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
