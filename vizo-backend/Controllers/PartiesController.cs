using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Controllers;

/// <summary>
/// Customers and suppliers -- the /parties screens.
///
/// A party is NOT a login. "Party" carries the trading record and shares its
/// primary key with "User", which is why creating one writes both rows inside a
/// single transaction: the User row with RequiresEmail = false (so the
/// ck_user_email_required check lets the e-mail be null) and the Party row that
/// hangs off it.
///
/// Controller-only by design: no DTO classes, no services, no interfaces, no
/// repositories. Request bodies bind to the records at the foot of the file.
/// Every action is wrapped in try/catch and reports through Fail().
/// </summary>
[Route("api/parties")]
[ApiController]
[Authorize(Policy = "Staff")]
public class PartiesController : ApiControllerBase
{
    public PartiesController(AppDbContext db, IConfiguration cfg,
        ILogger<PartiesController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

    /* TRAP: Party.SalesPersonUserId is a foreign key to "Employee", NOT to
       "User" -- so the navigation is an Employee and the name lives one hop
       further on at .SalesPersonUser.User.FullName. Reading .FullName straight
       off the navigation does not compile, which is the good outcome; the bad
       one is assuming it is a User id when filtering.

       Role ids: 5 = customer, 6 = supplier, 7 = customer & supplier.
       Kept as named constants rather than magic numbers scattered through the
       queries -- they come from the "Role" seed and never change. */
    private const int RoleCustomer = 5;
    private const int RoleSupplier = 6;
    private const int RoleBoth = 7;

    // ══════════════════════════════════════════════════════════════════
    //  LIST
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The party list. `type` is customer | supplier | all -- "customer" also
    /// returns the customer-and-supplier rows, because a shop that also supplies
    /// us is still a customer and must not vanish from the customer screen.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetParties(
        [FromQuery] string? type, [FromQuery] string? q,
        [FromQuery] bool includeInactive = true,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 50;

            var rows = _db.Parties.AsNoTracking().AsQueryable();

            rows = type?.ToLowerInvariant() switch
            {
                "customer" => rows.Where(p => p.User.RoleId == RoleCustomer || p.User.RoleId == RoleBoth),
                "supplier" => rows.Where(p => p.User.RoleId == RoleSupplier || p.User.RoleId == RoleBoth),
                _ => rows
            };

            if (!includeInactive)
                rows = rows.Where(p => p.User.IsActive);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(p =>
                    p.LegalName.ToLower().Contains(term) ||
                    p.PartyCode.ToLower().Contains(term) ||
                    (p.DisplayName != null && p.DisplayName.ToLower().Contains(term)) ||
                    (p.User.Phone != null && p.User.Phone.Contains(term)));
            }

            var total = await rows.CountAsync();

            var items = await rows
                .OrderBy(p => p.LegalName)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(p => new
                {
                    id = p.UserId,
                    partyCode = p.PartyCode,
                    type = p.User.RoleId == RoleSupplier ? "SUPPLIER"
                         : p.User.RoleId == RoleBoth ? "BOTH" : "CUSTOMER",
                    legalName = p.LegalName,
                    displayName = p.DisplayName ?? p.LegalName,
                    initials = "",
                    phone = p.User.Phone,
                    email = p.User.Email,
                    city = p.City.CityName,
                    province = p.City.Province.ProvinceName,
                    category = p.Category.CategoryKey,
                    categoryName = p.Category.CategoryName,
                    ntn = p.Ntn,
                    strn = p.Strn,
                    creditLimit = p.CreditLimit,
                    creditDays = p.CreditDays,
                    creditHoldPolicy = p.HoldPolicy.PolicyKey,
                    salesPerson = p.SalesPersonUser != null ? p.SalesPersonUser.User.FullName : null,
                    isActive = p.User.IsActive,
                    createdAt = p.User.CreatedAt,
                    rating = p.Rating.ToString(),

                    /* Receivable and payable are derived, never stored: the
                       posted ledger is the only place a balance is true. */
                    currentBalance = p.OpeningBalance + _db.JournalEntryLines
                        .Where(l => l.PartyUserId == p.UserId && l.Entry.StatusId == 2)
                        .Sum(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m,
                    payableBalance = _db.JournalEntryLines
                        .Where(l => l.PartyUserId == p.UserId && l.Entry.StatusId == 2)
                        .Sum(l => (decimal?)(l.CreditAmount - l.DebitAmount)) ?? 0m,

                    /* "When did we last do business with them" -- three separate
                       questions, and the party screens ask all three:
                         lastPurchaseAt -- they last bought from us
                         lastSupplyAt   -- they last supplied us
                         lastPaymentAt  -- they last actually paid
                       Each is a max() over the relevant document, not a stored
                       column, so it can never drift out of date. */
                    lastPurchaseAt = p.SalesInvoices
                        .Max(i => (DateOnly?)i.InvoiceDate),
                    lastSupplyAt = p.PurchaseInvoices
                        .Max(i => (DateOnly?)i.InvoiceDate),
                    lastPaymentAt = p.Collections
                        .Where(c => c.Status.StatusKey == "CONFIRMED")
                        .Max(c => (DateOnly?)c.CollectedOn)
                })
                .ToListAsync();

            /* Initials are a display concern, computed here so every screen
               shows the same two letters without each one reimplementing it. */
            var shaped = items.Select(p => new
            {
                p.id, p.partyCode, p.type, p.legalName, p.displayName,
                initials = Initials(p.displayName),
                p.phone, p.email, p.city, p.province, p.category, p.categoryName,
                p.ntn, p.strn, p.creditLimit, p.creditDays, p.creditHoldPolicy,
                p.salesPerson, p.isActive, p.createdAt, p.rating,
                p.currentBalance, p.payableBalance,
                p.lastPurchaseAt, p.lastSupplyAt, p.lastPaymentAt
            });

            return Ok(new { total, page, pageSize, items = shaped });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the party list");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  ONE PARTY
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetParty(int id)
    {
        try
        {
            var p = await _db.Parties.AsNoTracking()
                .Where(x => x.UserId == id)
                .Select(x => new
                {
                    id = x.UserId,
                    partyCode = x.PartyCode,
                    type = x.User.RoleId == RoleSupplier ? "SUPPLIER"
                         : x.User.RoleId == RoleBoth ? "BOTH" : "CUSTOMER",
                    legalName = x.LegalName,
                    displayName = x.DisplayName ?? x.LegalName,
                    phone = x.User.Phone,
                    altPhone = x.AltPhone,
                    email = x.User.Email,
                    cityId = x.CityId,
                    city = x.City.CityName,
                    province = x.City.Province.ProvinceName,
                    addressLine = x.AddressLine,
                    categoryId = x.CategoryId,
                    category = x.Category.CategoryKey,
                    categoryName = x.Category.CategoryName,
                    industry = x.Industry,
                    ntn = x.Ntn,
                    strn = x.Strn,
                    cnic = x.Cnic,
                    creditLimit = x.CreditLimit,
                    creditDays = x.CreditDays,
                    holdPolicyId = x.HoldPolicyId,
                    creditHoldPolicy = x.HoldPolicy.PolicyKey,
                    openingBalance = x.OpeningBalance,
                    salesPersonUserId = x.SalesPersonUserId,
                    salesPerson = x.SalesPersonUser != null ? x.SalesPersonUser.User.FullName : null,
                    defaultLocationId = x.DefaultLocationId,
                    rating = x.Rating.ToString(),
                    notes = x.Notes,
                    isActive = x.User.IsActive,
                    createdAt = x.User.CreatedAt,
                    orderCount = x.SalesOrders.Count,
                    invoiceCount = x.SalesInvoices.Count
                })
                .FirstOrDefaultAsync();

            if (p is null) return NotFound(new { message = $"No party with id {id}." });

            var balance = p.openingBalance + await _db.JournalEntryLines
                .Where(l => l.PartyUserId == id && l.Entry.StatusId == 2)
                .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;

            return Ok(new
            {
                p.id, p.partyCode, p.type, p.legalName, p.displayName,
                initials = Initials(p.displayName),
                p.phone, p.altPhone, p.email, p.cityId, p.city, p.province, p.addressLine,
                p.categoryId, p.category, p.categoryName, p.industry,
                p.ntn, p.strn, p.cnic,
                p.creditLimit, p.creditDays, p.holdPolicyId, p.creditHoldPolicy,
                p.openingBalance, p.salesPersonUserId, p.salesPerson, p.defaultLocationId,
                p.rating, p.notes, p.isActive, p.createdAt,
                p.orderCount, p.invoiceCount,
                currentBalance = balance
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load party {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  STATEMENT
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The customer statement: every posted ledger line against this party,
    /// oldest first, with a running balance. Only POSTED entries (StatusId 2)
    /// count -- a draft entry has not happened yet.
    /// </summary>
    [HttpGet("{id:int}/statement")]
    public async Task<IActionResult> GetStatement(int id,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            var party = await _db.Parties.AsNoTracking()
                .Where(p => p.UserId == id)
                .Select(p => new
                {
                    p.UserId, p.PartyCode, p.LegalName,
                    Display = p.DisplayName ?? p.LegalName,
                    p.OpeningBalance, p.CreditLimit, p.CreditDays,
                    Phone = p.User.Phone, City = p.City.CityName
                })
                .FirstOrDefaultAsync();

            if (party is null) return NotFound(new { message = $"No party with id {id}." });

            var q = _db.JournalEntryLines.AsNoTracking()
                .Where(l => l.PartyUserId == id && l.Entry.StatusId == 2);

            if (from is not null) q = q.Where(l => l.Entry.EntryDate >= from);
            if (to is not null) q = q.Where(l => l.Entry.EntryDate <= to);

            var lines = await q
                .OrderBy(l => l.Entry.EntryDate).ThenBy(l => l.LineId)
                .Select(l => new
                {
                    id = l.LineId,
                    date = l.Entry.EntryDate,
                    entryNo = l.Entry.EntryNo,
                    entryType = l.Entry.EntryType.TypeName,
                    reference = l.Entry.ReferenceNo,
                    narration = l.Description ?? l.Entry.Narration,
                    debit = l.DebitAmount,
                    credit = l.CreditAmount
                })
                .ToListAsync();

            /* Running balance is computed after the fetch: SQL window functions
               are not worth the round trip for a statement this size, and doing
               it here keeps the opening balance in one place. */
            var running = party.OpeningBalance;
            var rows = lines.Select(l =>
            {
                running += l.debit - l.credit;
                return new
                {
                    l.id, l.date, l.entryNo, l.entryType, l.reference, l.narration,
                    l.debit, l.credit, balance = running
                };
            }).ToList();

            return Ok(new
            {
                party = new
                {
                    id = party.UserId,
                    partyCode = party.PartyCode,
                    name = party.Display,
                    initials = Initials(party.Display),
                    phone = party.Phone,
                    city = party.City,
                    creditLimit = party.CreditLimit,
                    creditDays = party.CreditDays
                },
                openingBalance = party.OpeningBalance,
                closingBalance = running,
                totalDebit = rows.Sum(r => r.debit),
                totalCredit = rows.Sum(r => r.credit),
                lines = rows
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load the statement for party {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  VISITS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("visits")]
    public async Task<IActionResult> GetVisits([FromQuery] int take = 100)
    {
        try
        {
            if (take is < 1 or > 500) take = 100;

            var visits = await _db.CustomerVisits.AsNoTracking()
                .OrderByDescending(v => v.VisitedAt)
                .Take(take)
                .Select(v => new
                {
                    id = v.VisitId,
                    customerId = v.CustomerUserId,
                    customerName = v.CustomerUser.LegalName,
                    visitedAt = v.VisitedAt,
                    salesPerson = v.SalesPersonUser.User.FullName,
                    outcome = v.Outcome.OutcomeKey,
                    outcomeName = v.Outcome.OutcomeName,
                    note = v.Notes
                })
                .ToListAsync();

            return Ok(visits.Select(v => new
            {
                v.id, v.customerId, v.customerName,
                customerInitials = Initials(v.customerName),
                v.visitedAt, v.salesPerson, v.outcome, v.outcomeName, v.note
            }));
        }
        catch (Exception ex)
        {
            return Fail(ex, "load customer visits");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  LOOKUPS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Everything the party form's dropdowns need, in one round trip.</summary>
    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups()
    {
        try
        {
            return Ok(new
            {
                categories = await _db.PartyCategories.AsNoTracking()
                    .OrderBy(c => c.CategoryId)
                    .Select(c => new { id = c.CategoryId, key = c.CategoryKey, name = c.CategoryName })
                    .ToListAsync(),
                cities = await _db.Cities.AsNoTracking()
                    .OrderBy(c => c.CityName)
                    .Select(c => new { id = c.CityId, name = c.CityName, province = c.Province.ProvinceName })
                    .ToListAsync(),
                holdPolicies = await _db.CreditHoldPolicies.AsNoTracking()
                    .OrderBy(h => h.PolicyId)
                    .Select(h => new { id = h.PolicyId, key = h.PolicyKey, name = h.PolicyName })
                    .ToListAsync(),
                locations = await _db.Locations.AsNoTracking()
                    .Where(l => l.IsActive).OrderBy(l => l.LocationName)
                    .Select(l => new { id = l.LocationId, code = l.LocationCode, name = l.LocationName })
                    .ToListAsync(),
                salesPeople = await _db.Users.AsNoTracking()
                    .Where(u => u.Role.RoleKey == "sales" && u.IsActive)
                    .OrderBy(u => u.FullName)
                    .Select(u => new { id = u.UserId, name = u.FullName })
                    .ToListAsync()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load party lookups");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  CREATE / UPDATE
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates the User row and the Party row together. Both or neither --
    /// a Party with no User is unreachable and a User with no Party is a login
    /// that owns nothing, so this runs inside an explicit transaction.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "Staff")]
    public async Task<IActionResult> CreateParty([FromBody] PartyRequest body)
    {
        try
        {
            var error = await ValidateParty(body, null);
            if (error is not null) return BadRequest(new { message = error });

            var roleId = RoleFor(body.Type);

            await using var tx = await _db.Database.BeginTransactionAsync();

            var user = new User
            {
                RoleId = roleId,
                RequiresEmail = false,          // parties never sign in
                FullName = body.LegalName.Trim(),
                Email = string.IsNullOrWhiteSpace(body.Email) ? null : body.Email.Trim(),
                Phone = string.IsNullOrWhiteSpace(body.Phone) ? null : body.Phone.Trim(),
                PasswordHash = null,
                PrimaryLocationId = body.DefaultLocationId,
                IsActive = body.IsActive,
                CreatedAt = Today()
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync();       // need the generated UserId

            _db.Parties.Add(new Party
            {
                UserId = user.UserId,
                PartyCode = body.PartyCode.Trim().ToUpperInvariant(),
                LegalName = body.LegalName.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName.Trim(),
                CategoryId = body.CategoryId,
                CityId = body.CityId,
                AddressLine = body.AddressLine,
                AltPhone = body.AltPhone,
                Industry = body.Industry,
                Ntn = body.Ntn,
                Strn = body.Strn,
                Cnic = body.Cnic,
                CreditLimit = body.CreditLimit,
                CreditDays = body.CreditDays,
                HoldPolicyId = body.HoldPolicyId,
                OpeningBalance = body.OpeningBalance,
                SalesPersonUserId = body.SalesPersonUserId,
                DefaultLocationId = body.DefaultLocationId,
                Rating = string.IsNullOrWhiteSpace(body.Rating) ? 'C' : body.Rating.Trim()[0],
                Notes = body.Notes
            });
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("PARTY_CREATED", "Party", body.PartyCode,
                $"{body.LegalName} ({body.Type})", 1);

            return Ok(new { id = user.UserId, message = $"{body.LegalName} added." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "create the party");
        }
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = "Staff")]
    public async Task<IActionResult> UpdateParty(int id, [FromBody] PartyRequest body)
    {
        try
        {
            var party = await _db.Parties.FirstOrDefaultAsync(p => p.UserId == id);
            if (party is null) return NotFound(new { message = $"No party with id {id}." });

            var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == id);
            if (user is null) return NotFound(new { message = $"No user row behind party {id}." });

            var error = await ValidateParty(body, id);
            if (error is not null) return BadRequest(new { message = error });

            user.RoleId = RoleFor(body.Type);
            user.FullName = body.LegalName.Trim();
            user.Email = string.IsNullOrWhiteSpace(body.Email) ? null : body.Email.Trim();
            user.Phone = string.IsNullOrWhiteSpace(body.Phone) ? null : body.Phone.Trim();
            user.PrimaryLocationId = body.DefaultLocationId;
            user.IsActive = body.IsActive;

            party.PartyCode = body.PartyCode.Trim().ToUpperInvariant();
            party.LegalName = body.LegalName.Trim();
            party.DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName.Trim();
            party.CategoryId = body.CategoryId;
            party.CityId = body.CityId;
            party.AddressLine = body.AddressLine;
            party.AltPhone = body.AltPhone;
            party.Industry = body.Industry;
            party.Ntn = body.Ntn;
            party.Strn = body.Strn;
            party.Cnic = body.Cnic;
            party.CreditLimit = body.CreditLimit;
            party.CreditDays = body.CreditDays;
            party.HoldPolicyId = body.HoldPolicyId;
            party.OpeningBalance = body.OpeningBalance;
            party.SalesPersonUserId = body.SalesPersonUserId;
            party.DefaultLocationId = body.DefaultLocationId;
            party.Rating = string.IsNullOrWhiteSpace(body.Rating) ? 'C' : body.Rating.Trim()[0];
            party.Notes = body.Notes;

            await _db.SaveChangesAsync();
            await Log("PARTY_UPDATED", "Party", party.PartyCode, body.LegalName, 1);

            return Ok(new { id, message = $"{body.LegalName} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"save party {id}");
        }
    }

    [HttpPatch("{id:int}/active")]
    [Authorize(Policy = "Staff")]
    public async Task<IActionResult> SetActive(int id, [FromBody] ActiveRequest body)
    {
        try
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == id);
            if (user is null) return NotFound(new { message = $"No party with id {id}." });

            user.IsActive = body.Value;
            await _db.SaveChangesAsync();
            await Log(body.Value ? "PARTY_ACTIVATED" : "PARTY_DEACTIVATED",
                "Party", id.ToString(), user.FullName, 2);

            return Ok(new { id, isActive = body.Value });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"change the status of party {id}");
        }
    }

    // ════════════════════════ validation helpers ════════════════════════

    private static int RoleFor(string? type) => type?.ToUpperInvariant() switch
    {
        "SUPPLIER" => RoleSupplier,
        "BOTH" => RoleBoth,
        _ => RoleCustomer
    };

    private async Task<string?> ValidateParty(PartyRequest b, int? existingId)
    {
        if (string.IsNullOrWhiteSpace(b.LegalName)) return "Legal name is required.";
        if (string.IsNullOrWhiteSpace(b.PartyCode)) return "Party code is required.";
        if (b.CreditLimit < 0) return "Credit limit cannot be negative.";
        if (b.CreditDays < 0 || b.CreditDays > 365) return "Credit days must be between 0 and 365.";

        var code = b.PartyCode.Trim().ToUpperInvariant();
        var codeTaken = await _db.Parties
            .AnyAsync(p => p.PartyCode.ToUpper() == code && (existingId == null || p.UserId != existingId));
        if (codeTaken) return $"Party code {code} is already in use.";

        if (!string.IsNullOrWhiteSpace(b.Email))
        {
            var email = b.Email.Trim().ToLowerInvariant();
            var emailTaken = await _db.Users
                .AnyAsync(u => u.Email!.ToLower() == email && (existingId == null || u.UserId != existingId));
            if (emailTaken) return $"{b.Email} is already on another account.";
        }

        if (!await _db.PartyCategories.AnyAsync(c => c.CategoryId == b.CategoryId))
            return "Pick a valid category.";
        if (!await _db.Cities.AnyAsync(c => c.CityId == b.CityId))
            return "Pick a valid city.";
        if (!await _db.CreditHoldPolicies.AnyAsync(h => h.PolicyId == b.HoldPolicyId))
            return "Pick a valid credit-hold policy.";

        return null;
    }

    // ══════════════════════════ request bodies ══════════════════════════

    public record PartyRequest(
        string PartyCode, string LegalName, string? DisplayName, string Type,
        string? Email, string? Phone, string? AltPhone, string? AddressLine,
        int CategoryId, int CityId, string? Industry,
        string? Ntn, string? Strn, string? Cnic,
        decimal CreditLimit, int CreditDays, int HoldPolicyId, decimal OpeningBalance,
        int? SalesPersonUserId, int? DefaultLocationId, string? Rating, string? Notes,
        bool IsActive);

    public record ActiveRequest(bool Value);
}
