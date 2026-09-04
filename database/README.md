# AdvPOS — Database

PostgreSQL schema and seed data for the Advance POS System, built from the
frontend at [advpos-frontend](https://github.com/AmmarKamran2005/advpos-frontend).

## Files

| File | What it is | Required |
|---|---|---|
| `01_schema.sql` | 84 tables, keys and constraints | yes |
| `02_seed.sql` | 1,242 rows of the frontend's own mock data | yes |
| `03_sequence_reset.sql` | Moves identity sequences past the seeded ids | yes |
| `04_status_history.sql` | Status trail tables, kept separate on purpose | no |
| `ERD.txt` | Text ERD with the reasoning behind each decision | — |

## Load it

```bash
createdb advpos && psql -d advpos -v ON_ERROR_STOP=1 -f 01_schema.sql -f 02_seed.sql -f 03_sequence_reset.sql
```

`03_sequence_reset.sql` is not optional. The seed inserts explicit primary keys
so the rows read the way they read in the frontend, which leaves every identity
sequence at 1 — the first row your backend inserts would collide with seeded
row 1. Run it once, straight after the seed.

## Ground rules the schema follows

- PostgreSQL only. No enums — every closed value set is a lookup table with a
  parent→child foreign key. No triggers, no stored procedures, no functions.
- Normalised to 3NF. Repeating groups (barcodes, carriers, permissions,
  collection allocations) are child tables; transitive dependencies
  (city→province, account→type→group) are broken out.
- Table names PascalCase, so they must be double-quoted: `SELECT * FROM "User"`.
  Column names are lower snake_case and need no quoting.
- Primary and foreign keys only. No `CREATE INDEX`. `UNIQUE` appears only where
  a duplicate would be a real bug — e-mail, party code, SKU, document numbers.
- Money is `DECIMAL(14,2)`, percentages `DECIMAL(5,2)`. No `FLOAT` anywhere.
- Every foreign key is `ON UPDATE CASCADE`. On delete: `CASCADE` where the child
  is owned by the parent, `SET NULL` where the reference is optional. No foreign
  key ever blocks a delete.

## Two things worth knowing before you write the API

**The ledger is the only source of truth for money.** `Account` stores an
opening balance and nothing else — no running balance. Ledger, trial balance,
statements and both financial statements are all queries over `JournalEntry` +
`JournalEntryLine`. Likewise no invoice stores `paid` or `balance`; what an
invoice has been paid is `SUM(amount)` from `VoucherAllocation`.

**Deleting master data is destructive by design.** That follows from cascading
everywhere. Use the `is_active` flag on `Product`, `Party`, `Location`,
`Courier`, `Category`, `Brand` and `Account` instead of `DELETE`.

## Verified

Loaded against PostgreSQL 18. All of these were run and pass:

- every journal entry balances; trial balance 5,323,575.00 dr = 5,323,575.00 cr
- every document header equals the sum of its lines, to the paisa
- `subtotal + tax − discount = total` on every order and invoice, sales and purchase
- no invoice is allocated more money than it is worth
- every voucher's amount equals its journal entry
- every invoice's stored status agrees with what its allocations say
- the role→e-mail rule: staff cannot be saved without an e-mail, parties can,
  and a staff row cannot claim the party rule
- cascades: deleting an order removed its items, delivery, allocation and
  invoice with no error; deleting a location left all 32 users standing

The check queries are at the foot of `02_seed.sql`.
