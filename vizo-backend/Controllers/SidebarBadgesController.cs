using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// The sidebar's "new" badges on Orders and Customers (the owner, 30 Sep: "jaise
/// limit alerts mein 3 ka badge ... ishi tareeqe se orders ka bhi ... aur jab
/// usko click karke khol liya jaye to wo badge hat jana chahiye").
///
///   GET  /api/badges               -> { newOrders, newCustomers } for the caller
///   POST /api/badges/{area}/seen   -> the caller has opened that page; clear it
///
/// "New" means created after the newest one this person had seen when they last
/// opened the page -- a per-person marker (UserSeenMarker, migration 42), not a
/// global flag, because the accountant opening Orders must not clear the badge
/// on the owner's screen.
///
/// WHO GETS THEM: the Super Admin, the accountant and the order desk -- the
/// people new orders and customers arrive TO. A rep creates them; a badge
/// counting their own work back at them says nothing. Orders the caller keyed
/// in themselves are not "new" to them either.
///
/// A person with no marker yet starts at today's newest id, so the first
/// visit after the deploy shows 0 rather than every order ever taken.
/// </summary>
[Route("api/badges")]
[ApiController]
[Authorize(Policy = "Staff")]
public class SidebarBadgesController : ApiControllerBase
{
    private const string Orders = "orders";
    private const string Customers = "customers";

    /* Customer party roles, as PartiesController has them: 5 customer, 7 both. */
    private static readonly int[] CustomerRoles = { 5, 7 };

    public SidebarBadgesController(AppDbContext db, IConfiguration cfg,
        ILogger<SidebarBadgesController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

    private bool GetsBadges() => CurrentRole() is OrderWorkflow.RoleAdmin
        or OrderWorkflow.RoleAccountant or OrderWorkflow.RoleOrderDept;

    private async Task<int> NewestId(string area) => area == Orders
        ? await _db.SalesOrders.MaxAsync(o => (int?)o.OrderId) ?? 0
        : await _db.Parties.Where(p => CustomerRoles.Contains(p.User.RoleId))
            .MaxAsync(p => (int?)p.UserId) ?? 0;

    /// <summary>The caller's marker for one area, created at "now" if missing.</summary>
    private async Task<UserSeenMarker> MarkerFor(int me, string area)
    {
        var m = await _db.UserSeenMarkers.FirstOrDefaultAsync(x => x.UserId == me && x.Area == area);
        if (m is not null) return m;

        m = new UserSeenMarker { UserId = me, Area = area, LastSeenId = await NewestId(area), SeenAt = Now() };
        _db.UserSeenMarkers.Add(m);
        await _db.SaveChangesAsync();
        return m;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        try
        {
            if (!GetsBadges()) return Ok(new { newOrders = 0, newCustomers = 0 });

            var me = CurrentUserId();
            var orders = await MarkerFor(me, Orders);
            var customers = await MarkerFor(me, Customers);

            var newOrders = await _db.SalesOrders.AsNoTracking()
                .CountAsync(o => o.OrderId > orders.LastSeenId && o.CreatedByUserId != me
                                 && o.Status.StatusKey != OrderWorkflow.Draft);
            var newCustomers = await _db.Parties.AsNoTracking()
                .CountAsync(p => p.UserId > customers.LastSeenId && CustomerRoles.Contains(p.User.RoleId));

            return Ok(new { newOrders, newCustomers });
        }
        catch (Exception ex)
        {
            return Fail(ex, "count what is new");
        }
    }

    [HttpPost("{area}/seen")]
    public async Task<IActionResult> Seen(string area)
    {
        try
        {
            area = area.ToLowerInvariant();
            if (area is not (Orders or Customers))
                return BadRequest(new { message = $"Unknown area '{area}'." });
            if (!GetsBadges()) return Ok(new { area, cleared = false });

            var m = await MarkerFor(CurrentUserId(), area);
            m.LastSeenId = Math.Max(m.LastSeenId, await NewestId(area));
            m.SeenAt = Now();
            await _db.SaveChangesAsync();
            return Ok(new { area, cleared = true });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"clear the {area} badge");
        }
    }
}
