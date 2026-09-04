# AdvPOS — backend + frontend setup

Everything here was built and run against **.NET 8.0.419** and **PostgreSQL 18**
on Windows, and every endpoint listed in `API_CONTRACT.md` was called and
verified before this file was written.

---

## 1. NuGet packages

Exact versions used. Run these from your API project folder:

```bash
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL --version 8.0.11
dotnet add package Microsoft.EntityFrameworkCore.Design --version 8.0.11
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer --version 8.0.11
dotnet add package BCrypt.Net-Next --version 4.0.3
dotnet add package CloudinaryDotNet --version 1.27.0
dotnet add package MailKit --version 4.14.0
```

`Swashbuckle.AspNetCore` (6.6.2) and `Microsoft.AspNetCore.OpenApi` (8.0.25)
come with the `webapi` template — keep them.

Keep the EF tooling in step with the provider:

```bash
dotnet tool install --global dotnet-ef
```

**Version discipline matters here.** All the `Microsoft.*` / `Npgsql.*` packages
must stay on the same major as your target framework. Mixing an EF Core 9/10
package into a `net8.0` project produces runtime errors that look like
configuration bugs.

> **One honest note:** NuGet reports a *moderate* advisory (GHSA-9j88-vvj5-vhgr)
> against MailKit, and it is still open on 4.14.0 — the latest 4.x. It concerns
> parsing hostile MIME input. This app only *sends* mail and never parses
> untrusted messages, so the exposure is nil in this use. It will show as a
> `NU1902` warning on every build. Do not "fix" it by downgrading.

---

## 2. Files to copy into your existing project

| File | Purpose |
|---|---|
| `Program.cs` | EF, JWT bearer, role policies, CORS, Swagger-with-auth |
| `appsettings.json` | connection string, JWT, both Cloudinary accounts, SMTP |
| `Data/AppDbContext.cs` | scaffolded from the live database |
| `Models/*.cs` | 82 entity classes, scaffolded — identical to the ones you sent |
| `Controllers/AuthController.cs` | login, /me, forgot-password, verify-code, reset-password, change-password, logout |
| `Controllers/AdminController.cs` | every Super Admin endpoint |
| `Controllers/UploadController.cs` | Cloudinary images + PDFs |

The project's root namespace must be `vizo_backend` so the namespaces in these
files line up. In the `.csproj`:

```xml
<RootNamespace>vizo_backend</RootNamespace>
```

---

## 3. Two hand-edits that must survive a re-scaffold

If you ever run `dotnet ef dbcontext scaffold` again, these get overwritten and
things break in ways that are hard to trace. Both are commented in place.

### 3.1 `Data/AppDbContext.cs` — `Role.RequiresEmail`

```csharp
entity.Property(e => e.RequiresEmail)
    .HasDefaultValue(true)
    .ValueGeneratedNever()          // <-- ADD THIS
    .HasColumnName("requires_email");
```

`requires_email` is half of the principal key behind `fk_user_email_rule`, so
EF needs its value at INSERT time. `HasDefaultValue(true)` makes EF treat the
value `true` as "not set" and try to let the database supply it, which it
cannot do for a key. Without `ValueGeneratedNever()`, **creating any new role
throws** `"no value generator is available for properties of type 'bool'"`.

### 3.2 `Claim` and `Account` name collisions

`Claim` is a warranty claim in this domain and `Account` is a chart-of-accounts
row, so the framework types need aliases:

```csharp
// AuthController.cs
using SecurityClaim = System.Security.Claims.Claim;

// UploadController.cs
using CloudinaryAccount = CloudinaryDotNet.Account;
```

---

## 4. A trap in the scaffolded `User` model

`User` carries **two** location collections and they are easy to mix up:

| Property | Means |
|---|---|
| `User.Locations` | locations this person is **in charge of** (inverse of `Location.in_charge_user_id`) |
| `User.LocationsNavigation` | the `UserLocation` junction — locations they may **work out of** |

Access control wants the **second** one. Using the first silently returns the
wrong list; it cost a debugging round here before the users list came out right.

---

## 5. Database

```bash
createdb advpos
psql -d advpos -v ON_ERROR_STOP=1 \
  -f database/01_schema.sql \
  -f database/02_seed.sql \
  -f database/03_sequence_reset.sql \
  -f database/05_auth.sql
```

`05_auth.sql` is new and required for login. It adds:

- the **`PasswordResetCode`** table — the only table the forgot-password flow
  needed that the original schema had no reason to carry. It stores a **BCrypt
  hash of the six digits**, never the digits, plus an expiry and an attempt
  counter so a six-digit code cannot be walked through.
- real **BCrypt password hashes** on the staff rows, so people can sign in.

`04_status_history.sql` remains optional and unrelated.

Then point the API at it in `appsettings.json`:

```json
"ConnectionStrings": {
  "DefaultConnection": "Host=localhost;Port=5432;Database=advpos;Username=postgres;Password=postgres"
}
```

---

## 6. Configuration

Everything the backend needs is in **`vizo-backend/appsettings.json`**, which is
committed to this repository with its real values. Clone, restore, run -- there
is nothing to create and nothing to set first.

```jsonc
{
  "ConnectionStrings": { "DefaultConnection": "Host=...;Database=neondb;..." },
  "Jwt": {
    "Key": "...",                 // 64 chars, min 32 bytes for HMAC-SHA256
    "Issuer": "AdvPOS.Api",
    "Audience": "AdvPOS.Web",
    "ExpiryMinutes": 480
  },
  "Cors": { "AllowedOrigins": [ "http://localhost:3000", "https://www.vizo.com.pk" ] },
  "PasswordReset": { "CodeExpiryMinutes": 30, "MaxAttempts": 5 },

  "CloudinaryImages": { "CloudName": "dzzuoem1w", "ApiKey": "...", "ApiSecret": "...", "Folder": "advpos/images" },
  "CloudinaryPdfs":   { "CloudName": "dve3ucdo",  "ApiKey": "...", "ApiSecret": "...", "Folder": "advpos/documents" },

  "EmailSettings": {
    "SmtpHost": "smtp.gmail.com", "SmtpPort": 587,
    "SenderEmail": "vizo.com.pk@gmail.com",
    "SenderPassword": "...",      // Gmail App Password, not the account password
    "SenderName": "AdvPOS", "AdminAlertEmail": "vizo.com.pk@gmail.com"
  },

  "VapidSettings": {              // Web Push. Generate with:
    "Subject": "mailto:vizo.com.pk@gmail.com",   //   npx web-push generate-vapid-keys
    "PublicKey": "...",
    "PrivateKey": "..."
  },

  "Gemini": {                     // AI features. Key from aistudio.google.com/apikey
    "ApiKey": "",                 // empty = the AI panels say so and the reports still work
    "Model": "gemini-2.0-flash",
    "TimeoutSeconds": 30,
    "Enabled": true
  },

  "NightlyInsights": { "Enabled": true, "RunAt": "20:30" }
}
```

### Overriding for a deployment

Environment variables win over `appsettings.json` wherever they are set, using
the double-underscore form. Nothing has to be set -- this is only if a
deployment needs different values from the committed ones:

```
ConnectionStrings__DefaultConnection
Jwt__Key
CloudinaryImages__ApiSecret
CloudinaryPdfs__ApiSecret
EmailSettings__SenderPassword
VapidSettings__PrivateKey
Gemini__ApiKey
```

### Frontend

Frontend variables go in **`vizo-erp/.env.local`**, which is not committed.
Copy `vizo-erp/.env.example` and fill it in:

```
NEXT_PUBLIC_API_BASE_URL=https://localhost:7177/api
NEXT_PUBLIC_TOKEN_COOKIE=advpos_token
NEXT_PUBLIC_ROLE_COOKIE=advpos_role
NEXT_PUBLIC_VAPID_PUBLIC_KEY=<the same public key as VapidSettings:PublicKey>
```

`NEXT_PUBLIC_VAPID_PUBLIC_KEY` **must be the exact pair** of
`VapidSettings:PrivateKey` in `appsettings.json`. If they do not match, browsers
subscribe successfully and every push to them is then rejected -- which looks
like push simply not working.

### ⚠️ One thing to know

This repository is public, and the credentials above are in it and in its
history. Anyone who opens the repo can read the database password, the JWT
signing key, both Cloudinary secrets and the Gmail app password. That is a
deliberate choice by the project owner; if it ever needs undoing, the values
have to be rotated at each provider -- Neon, Cloudinary, Google -- because
removing them from the file does not remove them from the history.

---

## 7. Running it

```bash
# API
cd backend/vizo-backend
dotnet run                      # http://localhost:5185, Swagger at /swagger

# Frontend
cd vizo-erp
npm install                     # axios was added
npm run dev                     # http://localhost:3000
```

If you change the API port, change `NEXT_PUBLIC_API_BASE_URL` in
`vizo-erp/.env.local` **and** the `Cors:AllowedOrigins` entry.

---

## 8. Frontend setup

`vizo-erp/.env.local`:

```
NEXT_PUBLIC_API_BASE_URL=http://localhost:5185/api
NEXT_PUBLIC_TOKEN_COOKIE=advpos_token
NEXT_PUBLIC_ROLE_COOKIE=advpos_role
```

New dependency: `axios` (added to `package.json`).

New file: `src/proxy.ts` — route protection, see §9.

> Next.js 16 **renamed `middleware` to `proxy`** and deprecated the old name.
> The file must be `src/proxy.ts` and the export must be `proxy`, not
> `middleware`. Building with the old name only produces a deprecation warning
> today, but it is the convention that is going away.

Rewritten: `src/components/providers/session-provider.tsx` — real JWT session.
It exports the two helpers the page components use:

```tsx
import { API_BASE_URL, authHeader } from "@/components/providers/session-provider";

const res = await axios.get(`${API_BASE_URL}/admin/users`, { headers: authHeader() });
```

---

## 9. How authorisation actually works

There are two layers, and it is worth being clear about which one is load-bearing.

**`src/proxy.ts` — navigation only.** It reads the `advpos_role` cookie and
matches the path against a route table, so an accountant typing `/admin/users`
lands on `/forbidden` instead of watching a screen flash up and then fail. The
cookie is written by the browser and *could* be forged. That does not matter,
because of the second layer.

**The API — the real boundary.** Every `/api/admin/*` endpoint carries
`[Authorize(Policy = "SuperAdmin")]`, and the JWT signature is verified on every
single request. A forged cookie gets you a prettier 403, nothing more.

Route table (first match wins, unlisted routes are denied by default):

| Path | Roles |
|---|---|
| `/admin/**` | super-admin |
| `/accounting/**` | super-admin, accountant |
| `/sales/credit-holds` | super-admin, accountant |
| `/sales/direct` | super-admin, order-dept |
| `/sales/invoices`, `/sales/returns` | super-admin, accountant, order-dept |
| `/sales/**`, `/parties/**`, `/reports/**`, `/dashboard`, `/profile/**` | all four |
| `/purchases/**`, `/inventory/**` | super-admin, accountant, order-dept |
| `/packing`, `/dispatch` | super-admin, order-dept |
| `/delivery`, `/claims` | super-admin, order-dept, accountant |

The role claim value is the `role_key` column, so the database, the JWT and the
proxy all speak one vocabulary: `super-admin`, `accountant`, `order-dept`,
`sales`.

**Where each role lands after login** comes from `Role.home_path` in the
database — currently `/dashboard` for all four, which then renders the matching
portal. To give a role its own landing URL, just change that column; no code
changes needed.

---

## 10. Sign-in credentials

| Role | Email | Password |
|---|---|---|
| Super Admin | `vizo.com.pk@gmail.com` | `Admin@1234` |
| Accountant | `accounts@advpos.pk` | `Accounts@1234` |
| Order Department | `order@advpos.pk` | `Order@1234` |
| Sales | `sales@advpos.pk` | `Sales@1234` |

Other seeded staff — `nadia@`, `junaid@`, `ahmed@`, `imran@`, `sara@`,
`asad@vizo.com.pk` — all use `Vizo@1234`. (`asad@` is seeded inactive on
purpose, so it is a good way to check the deactivated-account path.)

These are development passwords set by `05_auth.sql`. Change them before this
goes anywhere real.

The login screen offers four shortcut buttons that fill in the **address only**.
It deliberately does not carry the passwords — a password shipped in the client
bundle is a password everybody has, and these are real accounts now.

---

## 11. Uploads

```
POST /api/upload/image?folder=products    multipart, field "file"
POST /api/upload/pdf?folder=invoices      multipart, field "file"
```

Images (max 5 MB: JPG/PNG/WEBP/GIF) go to the `dzzuoem1w` account.
PDFs (max 15 MB) go to `dve3ucdo` as a **raw** asset — a PDF must not be sent
through `ImageUploadParams` or Cloudinary tries to rasterise it.

Neither endpoint trusts the file extension. Both check magic bytes, so a
`.exe` renamed to `invoice.pdf` is refused. Verified: both accounts accepted a
real upload, and a text file renamed `.png` was rejected.

---

## 12. What was verified, and how

Run against a throwaway PostgreSQL 18 cluster seeded from `01`–`03` + `05`:

- all four roles sign in and receive a JWT carrying role + permission claims
  (39 / 26 / 23 / 5 permissions — matching the seed exactly)
- a `sales` token on `/api/admin/users` → **403**; no token → **401**; super-admin → **200**
- a customer account (`info@hafeezshop28.pk`) cannot sign in — parties are records, not logins
- full reset flow: code issued → wrong code burns an attempt → right code accepted →
  weak password refused → password changed → login with the new one → the code
  cannot be replayed
- create / update / delete verified for couriers, locations, users and roles,
  including the guard rails: duplicate name, duplicate email, duplicate employee
  code, duplicate document prefix, COD fee out of range, padding out of range,
  deleting a location that still holds stock, deleting a built-in role, and
  deactivating or locking the account you are signed in with
- the single-default-location invariant: making one default clears the others
- the audit log records these actions as they happen — it filled up with the
  test run itself

---

## 13. Known issues and follow-ups

**1. `react-hooks/set-state-in-effect` — 17 lint errors.**
`npx eslint src` reports 17 errors, all of that one rule, in the converted admin
pages and `session-provider.tsx`. They come from the pattern you asked for:

```tsx
const load = React.useCallback(async () => { setLoading(true); … }, []);
React.useEffect(() => { void load(); }, [load]);
```

`setLoading(true)` runs synchronously inside the effect body, which React 19's
lint rule flags. **Nothing is broken** — `npm run build` passes, `tsc` is clean
and every screen was driven in a browser. But if lint gates your CI, it fails.
Two ways out, whenever you want it:

- initialise `loading` to `true` and only call `setLoading(true)` on manual
  refetches, not on the first load; or
- move the fetch to the server per `AGENTS.md` rule 2, which is what that rule
  exists to push you toward.

**2. This conflicts with `AGENTS.md`.**
The repo mandates Server Components with server-side fetching and says plainly:
*"Never `useEffect` + `fetch` for the primary data of a page."* Your instruction
was axios + `useState`/`useEffect` inside the page files, so that is what was
built. It costs nothing extra in bundle terms — every admin page was already
`"use client"` — but those screens now fetch after hydration rather than
arriving with data. Worth a decision before the pattern spreads to the other
three portals.

**3. Only the Super Admin panel is dynamic.**
The Accountant, Order Department and Sales portals still read `src/data/*`
mocks. That was the scope. `src/data/mock.ts` is still imported by `top-bar.tsx`
for `quickCreate` — that one is static UI config (labels, icons, hrefs,
permission keys), not business data, so it was left alone.

**4. Things removed because nothing backs them.**
Rather than leave buttons that lie, these were taken out and the reason noted on
screen: backup restore-drills and schedules, the per-row backup Download, the
Settings page's Numbering / Tax / SMTP / Integrations tabs, the Website and
Industry company fields, the fabricated audit-log "request payload", and the
invented "3 active sessions" line. Each needs a table and an endpoint before it
can come back.

**5. The database used for verification was a throwaway.**
Everything here was proved against a temporary PostgreSQL 18 cluster on port
**55432**, created for this work and not part of your project. Point
`appsettings.json` at your own database and run the four SQL files from §5.
