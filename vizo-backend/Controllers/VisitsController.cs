using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// CUSTOMER VISITS -- logged by the rep, from a phone, at the shop (27 Sep, round E).
///
/// WHAT WAS THERE. "CustomerVisit" had every column a visit needs, and
/// PartiesController had a GET for it -- but nothing anywhere wrote a row. The
/// seven on live are seed data; the Visits screen could only ever show those
/// seven, and a rep had no way to say he had been anywhere.
///
/// WHO SEES WHAT (by ROLE, like PartiesController.MyPartiesOnly and
/// SalesController.SalesScopeUserId -- a permission ticked in Setup opens the
/// screen, it does not widen the book):
///   · a SALES REP logs visits to HIS customers only -- the accounts he opened
///     or has been assigned (Party.CreatedByUserId / SalesPersonUserId, the same
///     two facts the Customers screen uses) -- and sees only those customers'
///     visits, plus any he made himself to an account since handed to somebody
///     else (his own history does not vanish with the territory);
///   · the SUPER ADMIN sees every visit and may log one on a rep's behalf (a
///     rep who phoned it in); he names the rep;
///   · the ACCOUNTANT and the ORDER DESK read every visit and log none: a visit
///     is the sales team's work.
///
/// No money on any of it, so nothing is hidden from the order desk.
///
/// Controller-only by design, like every other controller here: no DTOs, no
/// services, request records at the foot. Every action in try/catch -> Fail().
/// </summary>
[Route("api/visits")]
[ApiController]
[Authorize(Policy = "Staff")]
public class VisitsController : ApiControllerBase
{
    /* Role ids of the party rows that are customers: 5 = customer, 7 = customer
       and supplier. Same constants PartiesController uses. */
    private const int RoleCustomer = 5;
    private const int RoleBoth = 7;

    /* How far back a rep may date a visit. Logging yesterday's round this
       morning is normal; a week is generous. Beyond that it is not a log, it is
       a reconstruction, and the Super Admin can still enter it. */
    private const int RepBackdateDays = 7;

    public VisitsController(AppDbContext db, IConfiguration cfg,
        ILogger<VisitsController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

    /// <summary>The rep the caller is scoped to, or null for the whole book.</summary>
    private int? RepScope() => CurrentRole() == OrderWorkflow.RoleSales ? CurrentUserId() : null;

    private bool MayLog() => CurrentRole() is OrderWorkflow.RoleSales or OrderWorkflow.RoleAdmin;

    // ══════════════════════════════════════════════════════════════════
    //  LIST
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Visits, newest first, with the figures the cards above the list show --
    /// counted here over everything that matches, not over the page the browser
    /// happened to receive.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetVisits(
        [FromQuery] int? customerId, [FromQuery] int? repId, [FromQuery] string? outcome,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? q,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 30)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 30;

            var rows = _db.CustomerVisits.AsNoTracking().AsQueryable();

            if (RepScope() is int me)
                rows = rows.Where(v => v.SalesPersonUserId == me ||
                                       v.CustomerUser.CreatedByUserId == me ||
                                       v.CustomerUser.SalesPersonUserId == me);

            if (customerId is not null) rows = rows.Where(v => v.CustomerUserId == customerId);
            if (repId is not null) rows = rows.Where(v => v.SalesPersonUserId == repId);
            if (!string.IsNullOrWhiteSpace(outcome)) rows = rows.Where(v => v.Outcome.OutcomeKey == outcome);
            if (from is not null)
            {
                var start = from.Value.ToDateTime(TimeOnly.MinValue);
                rows = rows.Where(v => v.VisitedAt >= start);
            }
            if (to is not null)
            {
                var end = to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue);
                rows = rows.Where(v => v.VisitedAt < end);
            }
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(v => (v.CustomerUser.DisplayName ?? v.CustomerUser.LegalName).ToLower().Contains(term) ||
                                       v.CustomerUser.PartyCode.ToLower().Contains(term) ||
                                       (v.Notes != null && v.Notes.ToLower().Contains(term)));
            }

            var today = Today();
            var monthStart = new DateTime(today.Year, today.Month, 1);
            var summary = await rows
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    total = g.Count(),
                    customersSeen = g.Select(v => v.CustomerUserId).Distinct().Count(),
                    reps = g.Select(v => v.SalesPersonUserId).Distinct().Count(),
                    ledToOrder = g.Count(v => v.Outcome.OutcomeKey == "ORDER_PLACED"),
                    thisMonth = g.Count(v => v.VisitedAt >= monthStart),
                    followUpsDue = g.Count(v => v.NextFollowUpDate != null && v.NextFollowUpDate <= today)
                })
                .FirstOrDefaultAsync();

            var items = await rows
                .OrderByDescending(v => v.VisitedAt).ThenByDescending(v => v.VisitId)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(v => new
                {
                    id = v.VisitId,
                    customerId = v.CustomerUserId,
                    customerName = v.CustomerUser.DisplayName ?? v.CustomerUser.LegalName,
                    customerCode = v.CustomerUser.PartyCode,
                    city = v.CustomerUser.City.CityName,
                    phone = v.CustomerUser.User.Phone,
                    visitedAt = v.VisitedAt,
                    loggedAt = v.LoggedAt,
                    salesPersonId = v.SalesPersonUserId,
                    salesPerson = v.SalesPersonUser.User.FullName,
                    outcome = v.Outcome.OutcomeKey,
                    outcomeName = v.Outcome.OutcomeName,
                    note = v.Notes,
                    nextFollowUp = v.NextFollowUpDate,
                    latitude = v.Latitude,
                    longitude = v.Longitude,
                    gpsAccuracyM = v.GpsAccuracyM
                })
                .ToListAsync();

            return Ok(new
            {
                total = summary?.total ?? 0,
                page, pageSize,
                summary = new
                {
                    visits = summary?.total ?? 0,
                    customersSeen = summary?.customersSeen ?? 0,
                    reps = summary?.reps ?? 0,
                    ledToOrder = summary?.ledToOrder ?? 0,
                    thisMonth = summary?.thisMonth ?? 0,
                    followUpsDue = summary?.followUpsDue ?? 0
                },
                mayLog = MayLog(),
                items = items.Select(v => new
                {
                    v.id, v.customerId, v.customerName, customerInitials = Initials(v.customerName),
                    v.customerCode, v.city, v.phone, v.visitedAt, v.loggedAt,
                    v.salesPersonId, v.salesPerson, v.outcome, v.outcomeName, v.note, v.nextFollowUp,
                    v.latitude, v.longitude, v.gpsAccuracyM,
                    followUpDue = v.nextFollowUp != null && v.nextFollowUp <= today,
                    /* Logged more than a day after the visit -- worth a glance, not an alarm. */
                    loggedLate = v.loggedAt != null && (v.loggedAt.Value - v.visitedAt).TotalHours > 24
                })
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load customer visits");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  WHAT THE FORM NEEDS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Outcomes from the VisitOutcome lookup (never a list typed into the
    /// screen), the customers the caller may log a visit to, and -- for the
    /// Super Admin -- the reps he may log one for.
    /// </summary>
    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups()
    {
        try
        {
            var customers = _db.Parties.AsNoTracking()
                .Where(p => (p.User.RoleId == RoleCustomer || p.User.RoleId == RoleBoth) && p.User.IsActive);
            if (RepScope() is int me)
                customers = customers.Where(p => p.CreatedByUserId == me || p.SalesPersonUserId == me);

            return Ok(new
            {
                mayLog = MayLog(),
                pickRep = CurrentRole() == OrderWorkflow.RoleAdmin,
                backdateDays = CurrentRole() == OrderWorkflow.RoleAdmin ? (int?)null : RepBackdateDays,
                outcomes = await _db.VisitOutcomes.AsNoTracking()
                    .OrderBy(o => o.OutcomeId)
                    .Select(o => new { id = o.OutcomeId, key = o.OutcomeKey, name = o.OutcomeName })
                    .ToListAsync(),
                customers = await customers
                    .OrderBy(p => p.DisplayName ?? p.LegalName)
                    .Select(p => new
                    {
                        id = p.UserId,
                        code = p.PartyCode,
                        name = p.DisplayName ?? p.LegalName,
                        city = p.City.CityName,
                        repId = p.SalesPersonUserId
                    })
                    .ToListAsync(),
                reps = CurrentRole() == OrderWorkflow.RoleAdmin
                    ? await _db.Employees.AsNoTracking()
                        .Where(e => e.User.IsActive && e.User.Role.RoleKey == OrderWorkflow.RoleSales)
                        .OrderBy(e => e.User.FullName)
                        .Select(e => new { id = e.UserId, name = e.User.FullName })
                        .ToListAsync()
                    : null
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the visit form");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  LOG A VISIT
    // ══════════════════════════════════════════════════════════════════

    [HttpPost]
    public async Task<IActionResult> LogVisit([FromBody] VisitRequest body)
    {
        try
        {
            if (!MayLog())
                return StatusCode(403, new { message = "Visits are logged by the sales team (or the Super Admin on a rep's behalf)." });

            var role = CurrentRole();
            var isAdmin = role == OrderWorkflow.RoleAdmin;

            var customer = await _db.Parties.AsNoTracking()
                .Where(p => p.UserId == body.CustomerId && (p.User.RoleId == RoleCustomer || p.User.RoleId == RoleBoth))
                .Select(p => new
                {
                    p.UserId, name = p.DisplayName ?? p.LegalName, p.CreatedByUserId, p.SalesPersonUserId,
                    active = p.User.IsActive
                })
                .FirstOrDefaultAsync();
            if (customer is null) return BadRequest(new { message = "Pick the customer you visited." });
            if (!customer.active) return BadRequest(new { message = $"{customer.name}'s account is switched off." });

            /* WHOSE VISIT. A rep logs his own, to his own customers -- checked here
               as well as in the lookup, because a list that hides a customer does
               not stop somebody posting its id. The Super Admin names the rep, or
               it falls to the customer's assigned rep. */
            int repId;
            if (isAdmin)
            {
                var chosen = body.SalesPersonUserId ?? customer.SalesPersonUserId;
                if (chosen is null)
                    return BadRequest(new { message = $"{customer.name} has no assigned rep. Say which rep made the visit." });
                if (!await _db.Employees.AnyAsync(e => e.UserId == chosen && e.User.IsActive && e.User.Role.RoleKey == OrderWorkflow.RoleSales))
                    return BadRequest(new { message = "The rep must be an active member of the sales team." });
                repId = chosen.Value;
            }
            else
            {
                repId = CurrentUserId();
                if (customer.CreatedByUserId != repId && customer.SalesPersonUserId != repId)
                    return StatusCode(403, new { message = $"{customer.name} is not one of your customers." });
                if (!await _db.Employees.AnyAsync(e => e.UserId == repId))
                    return BadRequest(new { message = "Your sign-in has no staff record, so a visit cannot be recorded against it." });
            }

            var outcome = await _db.VisitOutcomes.AsNoTracking().FirstOrDefaultAsync(o => o.OutcomeId == body.OutcomeId);
            if (outcome is null) return BadRequest(new { message = "Say what came of the visit." });

            /* WHEN. Not in the future (a few minutes' grace for a phone clock that
               runs fast); a rep may back-date a week, the Super Admin any amount. */
            var now = Now();
            /* The form sends the phone's wall-clock time with no zone, which is
               Pakistan time and binds as Unspecified -- what the column wants. A
               caller that sends UTC ("...Z") gets it moved to Pakistan time
               first: Npgsql refuses a Utc DateTime for this column (trap 6), and
               storing it unconverted would put the visit five hours early. */
            var visitedAt = body.VisitedAt ?? now;
            if (visitedAt.Kind == DateTimeKind.Utc)
                visitedAt = DateTime.SpecifyKind(visitedAt.AddHours(5), DateTimeKind.Unspecified);
            else if (visitedAt.Kind == DateTimeKind.Local)
                visitedAt = DateTime.SpecifyKind(visitedAt.ToUniversalTime().AddHours(5), DateTimeKind.Unspecified);
            visitedAt = visitedAt.AddTicks(-(visitedAt.Ticks % TimeSpan.TicksPerSecond));
            if (visitedAt > now.AddMinutes(10))
                return BadRequest(new { message = "A visit cannot be logged for a time that has not come yet." });
            if (!isAdmin && visitedAt < now.Date.AddDays(-RepBackdateDays))
                return BadRequest(new { message = $"A visit can be logged up to {RepBackdateDays} days after it happened. Ask the Super Admin to enter an older one." });

            var visitDay = DateOnly.FromDateTime(visitedAt);
            if (body.NextFollowUpDate is DateOnly next && next < visitDay)
                return BadRequest(new { message = "The follow-up cannot be before the visit." });
            if (outcome.OutcomeKey == "FOLLOWUP" && body.NextFollowUpDate is null)
                return BadRequest(new { message = "A follow-up needs the date you will go back." });

            /* WHERE. Optional -- a phone that refuses location must not stop the
               log -- but when it is sent it has to be a real place on the globe. */
            if ((body.Latitude is null) != (body.Longitude is null))
                return BadRequest(new { message = "Send both latitude and longitude, or neither." });
            if (body.Latitude is { } lat && (lat < -90 || lat > 90))
                return BadRequest(new { message = "Latitude must be between -90 and 90." });
            if (body.Longitude is { } lng && (lng < -180 || lng > 180))
                return BadRequest(new { message = "Longitude must be between -180 and 180." });

            var notes = Clean(body.Notes, 300);

            var visit = new CustomerVisit
            {
                CustomerUserId = customer.UserId,
                SalesPersonUserId = repId,
                VisitedAt = visitedAt,
                OutcomeId = outcome.OutcomeId,
                Notes = notes,
                NextFollowUpDate = body.NextFollowUpDate,
                /* numeric(9,6): six decimals is ~11 cm, far finer than any phone. */
                Latitude = body.Latitude is null ? null : Math.Round(body.Latitude.Value, 6),
                Longitude = body.Longitude is null ? null : Math.Round(body.Longitude.Value, 6),
                GpsAccuracyM = body.GpsAccuracyM is null ? null : Math.Max(0, (int)Math.Round(body.GpsAccuracyM.Value)),
                LoggedAt = now
            };
            _db.CustomerVisits.Add(visit);
            await _db.SaveChangesAsync();

            await Log("VISIT_LOGGED", "CustomerVisit", $"VISIT-{visit.VisitId}",
                $"{customer.name}: {outcome.OutcomeName} on {visitedAt:yyyy-MM-dd HH:mm}" +
                (body.NextFollowUpDate is null ? "" : $", back on {body.NextFollowUpDate:yyyy-MM-dd}") +
                (visit.Latitude is null ? "" : $", at {visit.Latitude},{visit.Longitude}"), 1);

            return Ok(new
            {
                id = visit.VisitId,
                message = $"Visit to {customer.name} logged: {outcome.OutcomeName}" +
                          (body.NextFollowUpDate is null ? "." : $", follow up on {body.NextFollowUpDate:dd MMM}.")
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "log the visit");
        }
    }

    private static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max ? s : s[..max];
    }

    // ══════════════════════════ request bodies ══════════════════════════

    public record VisitRequest(
        int CustomerId, DateTime? VisitedAt, int OutcomeId, string? Notes, DateOnly? NextFollowUpDate,
        decimal? Latitude, decimal? Longitude, double? GpsAccuracyM, int? SalesPersonUserId);
}
