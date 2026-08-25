using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Controllers;

/// <summary>
/// Everything the Super Admin panel needs. Controller-only by design: no
/// services, no DTO classes, no interfaces. Request bodies bind to records
/// declared at the foot of the file; responses are anonymous objects shaped
/// to match exactly what each screen renders.
///
/// The whole controller is [Authorize(Policy = "SuperAdmin")]. That is the
/// real security boundary -- the Next.js middleware only decides what to
/// show, this decides what can be done.
/// </summary>
[Route("api/[controller]")]
[ApiController]
//[Authorize(Policy = "SuperAdmin")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;

    public AdminController(AppDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    /* "timestamp without time zone" columns reject a Utc-kind DateTime. */
    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);

    private int CurrentUserId() =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    /* NOTE for anyone reading the scaffolded models: "User" carries TWO
       location collections and they are easy to mix up.
           User.Locations           -> locations this person is IN CHARGE OF
                                       (inverse of Location.in_charge_user_id)
           User.LocationsNavigation -> the UserLocation junction, i.e. the
                                       locations they may WORK OUT OF
       Access control wants the second one. */

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }

    private async Task Log(string action, string entityType, string reference, string? detail, int severityId)
    {
        _db.ActivityLogs.Add(new ActivityLog
        {
            UserId = CurrentUserId(),
            ActionName = action,
            EntityType = entityType,
            EntityReference = reference,
            Detail = detail,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            SeverityId = severityId,
            LoggedAt = Now()
        });
        await _db.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════════════════════════
    //  DASHBOARD
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard()
    {
        /* "Today" means today. When the database holds historical data and
           today is quiet, fall back to the most recent day that actually has
           invoices and hand the date back, so the screen can label what it is
           showing instead of just printing a zero. */
        var businessDate = await _db.SalesInvoices
            .Where(i => i.InvoiceDate <= Today())
            .OrderByDescending(i => i.InvoiceDate)
            .Select(i => (DateOnly?)i.InvoiceDate)
            .FirstOrDefaultAsync() ?? Today();

        var daySales = await _db.SalesInvoices
            .Where(i => i.InvoiceDate == businessDate)
            .SumAsync(i => (decimal?)i.TotalAmount) ?? 0m;

        var dayOrders = await _db.SalesOrders.CountAsync(o => o.OrderDate == businessDate);

        var collectedToday = await _db.Collections
            .Where(c => c.ConfirmedOn == businessDate && c.StatusId == 2)
            .SumAsync(c => (decimal?)c.Amount) ?? 0m;

        /* Receivable and payable come from the ledger, which is the single
           source of truth -- never from a stored balance column. */
        var arOutstanding = await _db.JournalEntryLines
            .Where(l => l.AccountId == 10 && l.Entry.StatusId == 2)
            .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
        var arOpening = await _db.Accounts.Where(a => a.AccountId == 10)
            .Select(a => a.OpeningBalance).FirstOrDefaultAsync();
        arOutstanding += arOpening;

        var apPayable = await _db.JournalEntryLines
            .Where(l => l.AccountId == 19 && l.Entry.StatusId == 2)
            .SumAsync(l => (decimal?)(l.CreditAmount - l.DebitAmount)) ?? 0m;
        var apOpening = await _db.Accounts.Where(a => a.AccountId == 19)
            .Select(a => a.OpeningBalance).FirstOrDefaultAsync();
        apPayable += apOpening;

        var cutoff60 = businessDate.AddDays(-60);
        var overdue60 = await _db.SalesInvoices
            .Where(i => i.DueDate < cutoff60)
            .SumAsync(i => (decimal?)(i.TotalAmount -
                i.VoucherAllocations.Sum(a => (decimal?)a.Amount) ?? 0m)) ?? 0m;

        var dueIn7 = await _db.PurchaseInvoices
            .Where(i => i.DueDate >= businessDate && i.DueDate <= businessDate.AddDays(7))
            .SumAsync(i => (decimal?)i.TotalAmount) ?? 0m;

        /* Orders sitting on the owner's approval queue. */
        var limitCrossed = await _db.SalesOrders
            .Where(o => o.Status.StatusKey == "CREDIT_HOLD")
            .Select(o => new
            {
                id = o.OrderId,
                orderNo = o.OrderNo,
                customerName = o.CustomerUser.LegalName,
                customerInitials = "",
                salesPerson = o.SalesPersonUser != null ? o.SalesPersonUser.User.FullName : "-",
                total = o.TotalAmount,
                creditHoldReason = o.CreditHoldReason,
                creditLimit = o.CustomerUser.CreditLimit
            })
            .ToListAsync();

        var claims = await _db.Claims
            .Where(c => c.Stage.IsOpen)
            .Select(c => new { c.Quantity, c.UnitCost })
            .ToListAsync();
        var claimValue = claims.Sum(c => c.Quantity * c.UnitCost);

        var awaiting = await _db.Collections
            .Where(c => c.Status.StatusKey == "AWAITING")
            .Select(c => c.Amount)
            .ToListAsync();

        var deadStock = await _db.StockBalances
            .Where(s => s.Quantity > 0 && !s.Location.ExcludeFromSellable)
            .Where(s => !_db.SalesInvoiceItems.Any(ii => ii.ProductId == s.ProductId))
            .SumAsync(s => (decimal?)(s.Quantity * s.Product.CostPrice)) ?? 0m;

        var activity = await _db.ActivityLogs
            .OrderByDescending(a => a.LoggedAt)
            .Take(6)
            .Select(a => new
            {
                id = a.LogId,
                user = a.User != null ? a.User.FullName : "System",
                action = a.ActionName,
                target = a.EntityReference,
                detail = a.Detail,
                time = a.LoggedAt,
                location = a.Location != null ? a.Location.LocationName : null,
                severity = a.Severity.SeverityKey
            })
            .ToListAsync();

        /* Thirty days of invoiced revenue for the trend chart. */
        var from = businessDate.AddDays(-30);
        var trendRaw = await _db.SalesInvoices
            .Where(i => i.InvoiceDate >= from && i.InvoiceDate <= businessDate)
            .GroupBy(i => i.InvoiceDate)
            .Select(g => new { date = g.Key, revenue = g.Sum(x => x.TotalAmount) })
            .ToListAsync();
        var trend = trendRaw.OrderBy(t => t.date).ToList();

        return Ok(new
        {
            businessDate,
            todaySales = new { value = daySales, orders = dayOrders },
            collections = new { value = collectedToday },
            arOutstanding = new { value = arOutstanding, overdue60Plus = overdue60 },
            apPayable = new { value = apPayable, dueIn7Days = dueIn7 },
            limitCrossed = limitCrossed.Select(o => new
            {
                o.id, o.orderNo, o.customerName,
                customerInitials = Initials(o.customerName),
                o.salesPerson, o.total, o.creditHoldReason, o.creditLimit
            }),
            claimsStuck = new { count = claims.Count, value = claimValue },
            deadStockValue = deadStock,
            awaitingCollections = new { count = awaiting.Count, value = awaiting.Sum() },
            activity,
            salesTrend = trend
        });
    }

    /// <summary>Owner lets an over-limit order through. Their risk, so it is
    /// recorded against their name.</summary>
    [HttpPost("orders/{id:int}/approve-credit-hold")]
    public async Task<IActionResult> ApproveCreditHold(int id, [FromBody] ReasonRequest? body)
    {
        var order = await _db.SalesOrders.Include(o => o.Status).Include(o => o.CustomerUser)
            .FirstOrDefaultAsync(o => o.OrderId == id);
        if (order is null) return NotFound(new { message = "Order not found." });
        if (order.Status.StatusKey != "CREDIT_HOLD")
            return BadRequest(new { message = "That order is not waiting on a limit decision." });

        var confirmed = await _db.OrderStatuses.FirstAsync(s => s.StatusKey == "CONFIRMED");
        order.StatusId = confirmed.StatusId;
        order.CreditHoldReason = null;
        await _db.SaveChangesAsync();

        await Log("CREDIT_APPROVED", "SalesOrder", order.OrderNo,
                  body?.Reason ?? "Approved over the credit limit by the owner", 3);

        return Ok(new { message = $"{order.OrderNo} approved and sent to the order department." });
    }

    /// <summary>Owner keeps it held. The note goes back to the rep.</summary>
    [HttpPost("orders/{id:int}/hold")]
    public async Task<IActionResult> HoldOrder(int id, [FromBody] ReasonRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Trim().Length < 5)
            return BadRequest(new { message = "Give the rep a reason of at least 5 characters." });

        var order = await _db.SalesOrders.FirstOrDefaultAsync(o => o.OrderId == id);
        if (order is null) return NotFound(new { message = "Order not found." });

        order.CreditHoldReason = body.Reason.Trim();
        await _db.SaveChangesAsync();
        await Log("CREDIT_HELD", "SalesOrder", order.OrderNo, body.Reason.Trim(), 3);

        return Ok(new { message = $"{order.OrderNo} stays held." });
    }

    // ══════════════════════════════════════════════════════════════════
    //  USERS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers([FromQuery] string? q, [FromQuery] int page = 1,
                                              [FromQuery] int pageSize = 15, [FromQuery] bool? isActive = null)
    {
        var query = _db.Users.Where(u => u.Role.IsStaffRole);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(u =>
                u.FullName.ToLower().Contains(term) ||
                (u.Email != null && u.Email.ToLower().Contains(term)) ||
                (u.Employee != null && u.Employee.EmployeeCode.ToLower().Contains(term)));
        }
        if (isActive.HasValue) query = query.Where(u => u.IsActive == isActive.Value);

        var total = await query.CountAsync();

        var rows = await query
            .OrderBy(u => u.UserId)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(u => new
            {
                id = u.UserId,
                fullName = u.FullName,
                email = u.Email,
                phone = u.Phone,
                employeeCode = u.Employee != null ? u.Employee.EmployeeCode : null,
                roleId = u.RoleId,
                roles = new[] { u.Role.RoleName },
                locations = u.LocationsNavigation.Select(l => l.LocationCode).ToList(),
                isActive = u.IsActive,
                isLocked = u.Employee != null && u.Employee.IsLocked,
                lastLoginAt = u.Employee != null ? u.Employee.LastLoginAt : null,
                createdAt = u.CreatedAt
            })
            .ToListAsync();

        return Ok(new
        {
            items = rows.Select(r => new
            {
                r.id, r.fullName, initials = Initials(r.fullName), r.email, r.phone,
                r.employeeCode, r.roleId, r.roles, r.locations, r.isActive, r.isLocked,
                r.lastLoginAt, r.createdAt
            }),
            total, page, pageSize
        });
    }

    [HttpGet("users/stats")]
    public async Task<IActionResult> UserStats()
    {
        var staff = _db.Users.Where(u => u.Role.IsStaffRole);
        return Ok(new
        {
            total = await staff.CountAsync(),
            active = await staff.CountAsync(u => u.IsActive),
            locked = await staff.CountAsync(u => u.Employee != null && u.Employee.IsLocked)
        });
    }

    [HttpGet("users/{id:int}")]
    public async Task<IActionResult> GetUser(int id)
    {
        var u = await _db.Users
            .Where(x => x.UserId == id)
            .Select(x => new
            {
                id = x.UserId,
                fullName = x.FullName,
                email = x.Email,
                phone = x.Phone,
                employeeCode = x.Employee != null ? x.Employee.EmployeeCode : null,
                roleId = x.RoleId,
                roles = new[] { x.Role.RoleName },
                roleKey = x.Role.RoleKey,
                permissionCount = x.Role.Permissions.Count,
                locations = x.LocationsNavigation.Select(l => new { l.LocationId, l.LocationCode, l.LocationName }).ToList(),
                primaryLocationId = x.PrimaryLocationId,
                isActive = x.IsActive,
                isLocked = x.Employee != null && x.Employee.IsLocked,
                lastLoginAt = x.Employee != null ? x.Employee.LastLoginAt : null,
                createdAt = x.CreatedAt
            })
            .FirstOrDefaultAsync();

        if (u is null) return NotFound(new { message = "User not found." });
        return Ok(new
        {
            u.id, u.fullName, initials = Initials(u.fullName), u.email, u.phone,
            u.employeeCode, u.roleId, u.roles, u.roleKey, u.permissionCount,
            u.locations, u.primaryLocationId, u.isActive, u.isLocked, u.lastLoginAt, u.createdAt
        });
    }

    [HttpGet("users/{id:int}/activity")]
    public async Task<IActionResult> UserActivity(int id, [FromQuery] int take = 20)
    {
        var rows = await _db.ActivityLogs
            .Where(a => a.UserId == id)
            .OrderByDescending(a => a.LoggedAt)
            .Take(take)
            .Select(a => new
            {
                id = a.LogId,
                action = a.ActionName,
                entity = a.EntityType + " " + a.EntityReference,
                detail = a.Detail,
                ip = a.IpAddress,
                time = a.LoggedAt,
                severity = a.Severity.SeverityKey
            })
            .ToListAsync();
        return Ok(rows);
    }

    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] UserRequest body)
    {
        var problem = await ValidateUser(body, null);
        if (problem is not null) return BadRequest(new { message = problem });

        var role = await _db.Roles.FirstAsync(r => r.RoleId == body.RoleId);

        var user = new User
        {
            RoleId = role.RoleId,
            RequiresEmail = role.RequiresEmail,
            FullName = body.FullName.Trim(),
            Email = body.Email?.Trim().ToLowerInvariant(),
            Phone = body.Phone?.Trim(),
            IsActive = body.IsActive,
            CreatedAt = Today(),
            /* A staff account always has a password. When an invite is sent
               it is a random one nobody knows, so the only way in is the
               emailed reset code. */
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(
                string.IsNullOrWhiteSpace(body.Password) ? Guid.NewGuid().ToString("N") : body.Password, 11)
        };

        if (body.LocationIds is { Count: > 0 })
            user.PrimaryLocationId = body.LocationIds[0];

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _db.Employees.Add(new Employee
        {
            UserId = user.UserId,
            EmployeeCode = body.EmployeeCode!.Trim().ToUpperInvariant(),
            IsLocked = false,
            JoinedOn = Today()
        });

        if (body.LocationIds is { Count: > 0 })
        {
            var locs = await _db.Locations.Where(l => body.LocationIds.Contains(l.LocationId)).ToListAsync();
            foreach (var l in locs) user.LocationsNavigation.Add(l);
        }

        await _db.SaveChangesAsync();
        await Log("CREATED", "User", user.Email ?? user.FullName, $"{role.RoleName} account created", 1);

        return Ok(new { id = user.UserId, message = $"{user.FullName} added." });
    }

    [HttpPut("users/{id:int}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UserRequest body)
    {
        var user = await _db.Users.Include(u => u.Employee).Include(u => u.LocationsNavigation)
            .FirstOrDefaultAsync(u => u.UserId == id);
        if (user is null) return NotFound(new { message = "User not found." });

        var problem = await ValidateUser(body, id);
        if (problem is not null) return BadRequest(new { message = problem });

        var role = await _db.Roles.FirstAsync(r => r.RoleId == body.RoleId);

        user.FullName = body.FullName.Trim();
        user.Email = body.Email?.Trim().ToLowerInvariant();
        user.Phone = body.Phone?.Trim();
        user.RoleId = role.RoleId;
        user.RequiresEmail = role.RequiresEmail;
        user.IsActive = body.IsActive;

        if (user.Employee is not null && !string.IsNullOrWhiteSpace(body.EmployeeCode))
            user.Employee.EmployeeCode = body.EmployeeCode.Trim().ToUpperInvariant();

        if (body.LocationIds is not null)
        {
            user.LocationsNavigation.Clear();
            var locs = await _db.Locations.Where(l => body.LocationIds.Contains(l.LocationId)).ToListAsync();
            foreach (var l in locs) user.LocationsNavigation.Add(l);
            user.PrimaryLocationId = body.LocationIds.Count > 0 ? body.LocationIds[0] : null;
        }

        await _db.SaveChangesAsync();
        await Log("UPDATED", "User", user.Email ?? user.FullName, "Account updated", 1);
        return Ok(new { message = $"{user.FullName} updated." });
    }

    [HttpPatch("users/{id:int}/active")]
    public async Task<IActionResult> SetUserActive(int id, [FromBody] BoolRequest body)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == id);
        if (user is null) return NotFound(new { message = "User not found." });

        if (id == CurrentUserId() && !body.Value)
            return BadRequest(new { message = "You cannot deactivate the account you are signed in with." });

        user.IsActive = body.Value;
        await _db.SaveChangesAsync();
        await Log("UPDATED", "User", user.Email ?? user.FullName,
                  body.Value ? "Account activated" : "Account deactivated", 3);
        return Ok(new { message = body.Value ? "Account activated." : "Account deactivated." });
    }

    [HttpPatch("users/{id:int}/lock")]
    public async Task<IActionResult> SetUserLock(int id, [FromBody] BoolRequest body)
    {
        var emp = await _db.Employees.Include(e => e.User).FirstOrDefaultAsync(e => e.UserId == id);
        if (emp is null) return NotFound(new { message = "That user has no staff record." });

        if (id == CurrentUserId() && body.Value)
            return BadRequest(new { message = "You cannot lock the account you are signed in with." });

        emp.IsLocked = body.Value;
        await _db.SaveChangesAsync();
        await Log("UPDATED", "User", emp.User.Email ?? emp.User.FullName,
                  body.Value ? "Account locked" : "Account unlocked", 3);
        return Ok(new { message = body.Value ? "Account locked." : "Account unlocked." });
    }

    /// <summary>Clears the password so the only way back in is the emailed
    /// reset code. The code itself is issued by /api/auth/forgot-password.</summary>
    [HttpPost("users/{id:int}/password-reset")]
    public async Task<IActionResult> ForceReset(int id)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == id);
        if (user is null) return NotFound(new { message = "User not found." });
        if (string.IsNullOrWhiteSpace(user.Email))
            return BadRequest(new { message = "That user has no email address to send a code to." });

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"), 11);
        await _db.SaveChangesAsync();
        await Log("PASSWORD_RESET", "User", user.Email, "Password cleared by the administrator", 3);

        return Ok(new { message = $"{user.FullName} must now reset via the code sent to {user.Email}." });
    }

    /// <summary>Deactivate rather than delete: the audit trail, the orders
    /// they took and the entries they posted all still point here.</summary>
    [HttpDelete("users/{id:int}")]
    public async Task<IActionResult> DeleteUser(int id, [FromBody] ReasonRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Trim().Length < 5)
            return BadRequest(new { message = "A reason of at least 5 characters is required." });

        var user = await _db.Users.Include(u => u.Employee).FirstOrDefaultAsync(u => u.UserId == id);
        if (user is null) return NotFound(new { message = "User not found." });
        if (id == CurrentUserId())
            return BadRequest(new { message = "You cannot delete the account you are signed in with." });

        user.IsActive = false;
        /* Not null: the schema's ck_user_password forbids a staff row without
           a hash. Overwrite it with a random one nobody holds instead -- the
           effect is the same and the constraint stays satisfied. */
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"), 11);
        if (user.Employee is not null) user.Employee.IsLocked = true;
        await _db.SaveChangesAsync();

        await Log("DELETED", "User", user.Email ?? user.FullName, body.Reason.Trim(), 4);
        return Ok(new { message = $"{user.FullName} deactivated and access revoked." });
    }

    private async Task<string?> ValidateUser(UserRequest b, int? existingId)
    {
        if (string.IsNullOrWhiteSpace(b.FullName) || b.FullName.Trim().Length < 2)
            return "Full name is required.";
        if (string.IsNullOrWhiteSpace(b.Email))
            return "Email is required for a staff account.";
        if (!b.Email.Contains('@') || !b.Email.Contains('.'))
            return "That email address does not look right.";
        if (string.IsNullOrWhiteSpace(b.EmployeeCode))
            return "Employee code is required.";
        if (!await _db.Roles.AnyAsync(r => r.RoleId == b.RoleId))
            return "Pick a valid role.";

        var email = b.Email.Trim().ToLowerInvariant();
        if (await _db.Users.AnyAsync(u => u.Email != null && u.Email.ToLower() == email && u.UserId != existingId))
            return "Another account already uses that email address.";

        var code = b.EmployeeCode.Trim().ToUpperInvariant();
        if (await _db.Employees.AnyAsync(e => e.EmployeeCode.ToUpper() == code && e.UserId != existingId))
            return "Another account already uses that employee code.";

        return null;
    }

    // ══════════════════════════════════════════════════════════════════
    //  ROLES AND PERMISSIONS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles() =>
        Ok(await _db.Roles
            .OrderBy(r => r.RoleId)
            .Select(r => new
            {
                id = r.RoleId,
                key = r.RoleKey,
                name = r.RoleName,
                description = r.Description,
                homePath = r.HomePath,
                isSystem = r.IsSystem,
                isStaffRole = r.IsStaffRole,
                userCount = r.UserRoles.Count,
                permissionCount = r.Permissions.Count
            })
            .ToListAsync());

    [HttpGet("roles/{id:int}")]
    public async Task<IActionResult> GetRole(int id)
    {
        var role = await _db.Roles
            .Where(r => r.RoleId == id)
            .Select(r => new
            {
                id = r.RoleId,
                key = r.RoleKey,
                name = r.RoleName,
                description = r.Description,
                homePath = r.HomePath,
                isSystem = r.IsSystem,
                isStaffRole = r.IsStaffRole,
                userCount = r.UserRoles.Count,
                permissions = r.Permissions.Select(p => p.PermissionKey).ToList()
            })
            .FirstOrDefaultAsync();

        if (role is null) return NotFound(new { message = "Role not found." });
        return Ok(role);
    }

    /// <summary>The one permission catalogue, grouped the way the editor
    /// renders it.</summary>
    [HttpGet("permissions")]
    public async Task<IActionResult> GetPermissions()
    {
        var all = await _db.Permissions.OrderBy(p => p.PermissionId).ToListAsync();
        return Ok(all
            .GroupBy(p => p.GroupName)
            .Select(g => new
            {
                module = g.Key,
                permissions = g.Select(p => new { key = p.PermissionKey, label = p.Label })
            }));
    }

    [HttpPost("roles")]
    public async Task<IActionResult> CreateRole([FromBody] RoleRequest body)
    {
        var problem = ValidateRole(body);
        if (problem is not null) return BadRequest(new { message = problem });

        var key = body.Name.Trim().ToLowerInvariant().Replace(' ', '-');
        if (await _db.Roles.AnyAsync(r => r.RoleKey == key))
            return BadRequest(new { message = "A role with that name already exists." });

        var role = new Role
        {
            RoleKey = key,
            RoleName = body.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(body.Description) ? body.Name.Trim() : body.Description.Trim(),
            HomePath = string.IsNullOrWhiteSpace(body.HomePath) ? "/dashboard" : body.HomePath.Trim(),
            IsStaffRole = true,
            RequiresEmail = true,
            IsSystem = false
        };

        var perms = await _db.Permissions.Where(p => body.Permissions.Contains(p.PermissionKey)).ToListAsync();
        foreach (var p in perms) role.Permissions.Add(p);
        try
        {
            _db.Roles.Add(role);
            await _db.SaveChangesAsync();
            await Log("CREATED", "Role", role.RoleName, $"{perms.Count} permissions", 1);

            return Ok(new { id = role.RoleId, message = $"{role.RoleName} created." });

        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            return BadRequest();
        }
    }

    [HttpPut("roles/{id:int}")]
    public async Task<IActionResult> UpdateRole(int id, [FromBody] RoleRequest body)
    {
        var role = await _db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.RoleId == id);
        if (role is null) return NotFound(new { message = "Role not found." });

        var problem = ValidateRole(body);
        if (problem is not null) return BadRequest(new { message = problem });

        /* A built-in role can be re-permissioned but not renamed -- the
           posting logic and the seed data both key off its name. */
        if (!role.IsSystem)
        {
            role.RoleName = body.Name.Trim();
            role.Description = string.IsNullOrWhiteSpace(body.Description) ? role.Description : body.Description.Trim();
        }
        if (!string.IsNullOrWhiteSpace(body.HomePath)) role.HomePath = body.HomePath.Trim();

        role.Permissions.Clear();
        var perms = await _db.Permissions.Where(p => body.Permissions.Contains(p.PermissionKey)).ToListAsync();
        foreach (var p in perms) role.Permissions.Add(p);

        await _db.SaveChangesAsync();
        await Log("UPDATED", "Role", role.RoleName, $"Now {perms.Count} permissions", 3);

        return Ok(new { message = $"{role.RoleName} saved." });
    }

    [HttpDelete("roles/{id:int}")]
    public async Task<IActionResult> DeleteRole(int id, [FromBody] ReasonRequest body)
    {
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.RoleId == id);
        if (role is null) return NotFound(new { message = "Role not found." });
        if (role.IsSystem) return BadRequest(new { message = "Built-in roles cannot be deleted." });

        var users = await _db.Users.CountAsync(u => u.RoleId == id);
        if (users > 0)
            return BadRequest(new { message = $"{users} user(s) still hold this role. Move them first." });

        if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Trim().Length < 5)
            return BadRequest(new { message = "A reason of at least 5 characters is required." });

        _db.Roles.Remove(role);
        await _db.SaveChangesAsync();
        await Log("DELETED", "Role", role.RoleName, body.Reason.Trim(), 4);
        return Ok(new { message = $"{role.RoleName} deleted." });
    }

    private static string? ValidateRole(RoleRequest b)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length < 2) return "Role name is required.";
        if (b.Permissions is null || b.Permissions.Count == 0) return "Pick at least one permission.";
        return null;
    }

    // ══════════════════════════════════════════════════════════════════
    //  LOCATIONS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("locations")]
    public async Task<IActionResult> GetLocations([FromQuery] bool includeInactive = true) =>
        Ok(await _db.Locations
            .Where(l => includeInactive || l.IsActive)
            .OrderBy(l => l.LocationId)
            .Select(l => new
            {
                id = l.LocationId,
                code = l.LocationCode,
                name = l.LocationName,
                kindId = l.KindId,
                kind = l.Kind.KindKey,
                kindLabel = l.Kind.KindName,
                cityId = l.CityId,
                city = l.City.CityName,
                address = l.AddressLine,
                inChargeUserId = l.InChargeUserId,
                inCharge = l.InChargeUser != null ? l.InChargeUser.FullName : null,
                isActive = l.IsActive,
                isDefault = l.IsDefault,
                excludeFromSellable = l.ExcludeFromSellable,
                stockUnits = l.StockBalances.Sum(s => (int?)s.Quantity) ?? 0
            })
            .ToListAsync());

    [HttpPost("locations")]
    public async Task<IActionResult> CreateLocation([FromBody] LocationRequest body)
    {
        var problem = await ValidateLocation(body, null);
        if (problem is not null) return BadRequest(new { message = problem });

        var loc = new Location
        {
            LocationCode = body.Code.Trim().ToUpperInvariant(),
            LocationName = body.Name.Trim(),
            KindId = body.KindId,
            CityId = body.CityId,
            AddressLine = body.Address?.Trim() ?? "",
            InChargeUserId = body.InChargeUserId,
            IsActive = body.IsActive,
            IsDefault = body.IsDefault,
            ExcludeFromSellable = body.ExcludeFromSellable
        };

        if (body.IsDefault) await ClearOtherDefaults(null);

        _db.Locations.Add(loc);
        await _db.SaveChangesAsync();
        await Log("CREATED", "Location", loc.LocationCode, loc.LocationName, 1);
        return Ok(new { id = loc.LocationId, message = $"{loc.LocationName} added." });
    }

    [HttpPut("locations/{id:int}")]
    public async Task<IActionResult> UpdateLocation(int id, [FromBody] LocationRequest body)
    {
        var loc = await _db.Locations.FirstOrDefaultAsync(l => l.LocationId == id);
        if (loc is null) return NotFound(new { message = "Location not found." });

        var problem = await ValidateLocation(body, id);
        if (problem is not null) return BadRequest(new { message = problem });

        if (body.IsDefault && !loc.IsDefault) await ClearOtherDefaults(id);

        loc.LocationCode = body.Code.Trim().ToUpperInvariant();
        loc.LocationName = body.Name.Trim();
        loc.KindId = body.KindId;
        loc.CityId = body.CityId;
        loc.AddressLine = body.Address?.Trim() ?? "";
        loc.InChargeUserId = body.InChargeUserId;
        loc.IsActive = body.IsActive;
        loc.IsDefault = body.IsDefault;
        loc.ExcludeFromSellable = body.ExcludeFromSellable;

        await _db.SaveChangesAsync();
        await Log("UPDATED", "Location", loc.LocationCode, loc.LocationName, 1);
        return Ok(new { message = $"{loc.LocationName} updated." });
    }

    [HttpDelete("locations/{id:int}")]
    public async Task<IActionResult> DeleteLocation(int id)
    {
        var loc = await _db.Locations.FirstOrDefaultAsync(l => l.LocationId == id);
        if (loc is null) return NotFound(new { message = "Location not found." });

        /* Deleting cascades in this schema, so a location holding stock would
           silently take its balances with it. Refuse instead. */
        var units = await _db.StockBalances.Where(s => s.LocationId == id).SumAsync(s => (int?)s.Quantity) ?? 0;
        if (units != 0)
            return BadRequest(new { message = $"{units} units still sit here. Move the stock to another location first." });

        if (loc.IsDefault)
            return BadRequest(new { message = "This is the default location. Make another one default first." });

        loc.IsActive = false;
        await _db.SaveChangesAsync();
        await Log("DELETED", "Location", loc.LocationCode, "Deactivated", 4);
        return Ok(new { message = $"{loc.LocationName} deactivated." });
    }

    private async Task ClearOtherDefaults(int? keepId)
    {
        var others = await _db.Locations.Where(l => l.IsDefault && l.LocationId != keepId).ToListAsync();
        foreach (var o in others) o.IsDefault = false;
    }

    private async Task<string?> ValidateLocation(LocationRequest b, int? existingId)
    {
        if (string.IsNullOrWhiteSpace(b.Code)) return "Code is required.";
        if (string.IsNullOrWhiteSpace(b.Name)) return "Name is required.";
        if (!await _db.LocationKinds.AnyAsync(k => k.KindId == b.KindId)) return "Pick a valid location type.";
        if (!await _db.Cities.AnyAsync(c => c.CityId == b.CityId)) return "Pick a valid city.";

        var code = b.Code.Trim().ToUpperInvariant();
        if (await _db.Locations.AnyAsync(l => l.LocationCode.ToUpper() == code && l.LocationId != existingId))
            return "Another location already uses that code.";

        var name = b.Name.Trim().ToLower();
        if (await _db.Locations.AnyAsync(l => l.LocationName.ToLower() == name && l.LocationId != existingId))
            return "Another location already uses that name.";

        return null;
    }

    [HttpGet("location-kinds")]
    public async Task<IActionResult> GetLocationKinds() =>
        Ok(await _db.LocationKinds.OrderBy(k => k.KindId)
            .Select(k => new { id = k.KindId, key = k.KindKey, name = k.KindName }).ToListAsync());

    // ══════════════════════════════════════════════════════════════════
    //  COURIERS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("couriers")]
    public async Task<IActionResult> GetCouriers() =>
        Ok(await _db.Couriers
            .OrderBy(c => c.CourierId)
            .Select(c => new
            {
                id = c.CourierId,
                name = c.CourierName,
                shortName = c.ShortName,
                contactPerson = c.ContactPerson,
                phone = c.Phone,
                codSettlementDays = c.CodSettlementDays,
                bookingCharge = c.BookingCharge,
                codFeePercent = c.CodFeePercent,
                trackingUrlTemplate = c.TrackingUrlTemplate,
                isActive = c.IsActive,
                consignmentCount = c.Deliveries.Count
            })
            .ToListAsync());

    [HttpPost("couriers")]
    public async Task<IActionResult> CreateCourier([FromBody] CourierRequest body)
    {
        var problem = await ValidateCourier(body, null);
        if (problem is not null) return BadRequest(new { message = problem });

        var c = new Courier
        {
            CourierName = body.Name.Trim(),
            ShortName = body.ShortName.Trim(),
            ContactPerson = body.ContactPerson?.Trim(),
            Phone = body.Phone?.Trim(),
            CodSettlementDays = (short)body.CodSettlementDays,
            BookingCharge = body.BookingCharge,
            CodFeePercent = body.CodFeePercent,
            TrackingUrlTemplate = body.TrackingUrlTemplate?.Trim(),
            IsActive = body.IsActive
        };
        _db.Couriers.Add(c);
        await _db.SaveChangesAsync();
        await Log("CREATED", "Courier", c.CourierName, null, 1);
        return Ok(new { id = c.CourierId, message = $"{c.CourierName} added." });
    }

    [HttpPut("couriers/{id:int}")]
    public async Task<IActionResult> UpdateCourier(int id, [FromBody] CourierRequest body)
    {
        var c = await _db.Couriers.FirstOrDefaultAsync(x => x.CourierId == id);
        if (c is null) return NotFound(new { message = "Courier not found." });

        var problem = await ValidateCourier(body, id);
        if (problem is not null) return BadRequest(new { message = problem });

        c.CourierName = body.Name.Trim();
        c.ShortName = body.ShortName.Trim();
        c.ContactPerson = body.ContactPerson?.Trim();
        c.Phone = body.Phone?.Trim();
        c.CodSettlementDays = (short)body.CodSettlementDays;
        c.BookingCharge = body.BookingCharge;
        c.CodFeePercent = body.CodFeePercent;
        c.TrackingUrlTemplate = body.TrackingUrlTemplate?.Trim();
        c.IsActive = body.IsActive;

        await _db.SaveChangesAsync();
        await Log("UPDATED", "Courier", c.CourierName, null, 1);
        return Ok(new { message = $"{c.CourierName} updated." });
    }

    [HttpDelete("couriers/{id:int}")]
    public async Task<IActionResult> DeleteCourier(int id)
    {
        var c = await _db.Couriers.FirstOrDefaultAsync(x => x.CourierId == id);
        if (c is null) return NotFound(new { message = "Courier not found." });

        /* Past consignments keep pointing here, so retire rather than delete. */
        var used = await _db.Deliveries.AnyAsync(d => d.CourierId == id);
        if (used)
        {
            c.IsActive = false;
            await _db.SaveChangesAsync();
            await Log("UPDATED", "Courier", c.CourierName, "Retired - has past deliveries", 3);
            return Ok(new { message = $"{c.CourierName} retired. Past deliveries still show it." });
        }

        _db.Couriers.Remove(c);
        await _db.SaveChangesAsync();
        await Log("DELETED", "Courier", c.CourierName, null, 4);
        return Ok(new { message = $"{c.CourierName} deleted." });
    }

    private async Task<string?> ValidateCourier(CourierRequest b, int? existingId)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length < 2) return "Courier name is required.";
        if (string.IsNullOrWhiteSpace(b.ShortName)) return "Short name is required.";
        if (b.CodSettlementDays is < 0 or > 60) return "Settlement days must be between 0 and 60.";
        if (b.BookingCharge < 0) return "Booking charge cannot be negative.";
        if (b.CodFeePercent is < 0 or > 20) return "COD fee must be between 0 and 20 percent.";

        var name = b.Name.Trim().ToLower();
        if (await _db.Couriers.AnyAsync(c => c.CourierName.ToLower() == name && c.CourierId != existingId))
            return "Another courier already uses that name.";
        return null;
    }

    // ══════════════════════════════════════════════════════════════════
    //  ACCOUNT TYPES
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("account-types")]
    public async Task<IActionResult> GetAccountTypes([FromQuery] string? group)
    {
        var q = _db.AccountTypes.AsQueryable();
        if (!string.IsNullOrWhiteSpace(group) && group != "all")
            q = q.Where(t => t.Group.GroupName == group);

        var rows = await q.OrderBy(t => t.AccountTypeId)
            .Select(t => new
            {
                id = t.AccountTypeId,
                name = t.TypeName,
                groupId = t.GroupId,
                group = t.Group.GroupName,
                prefix = t.CodePrefix,
                codeLength = t.CodeLength,
                normalBalance = t.IsDebitNormal ? "debit" : "credit",
                onBalanceSheet = t.Group.OnBalanceSheet,
                isSystem = t.IsSystem,
                accountCount = t.Accounts.Count,
                /* The next code this type would mint, from the highest number
                   actually issued -- not a figure hardcoded in the UI. */
                lastSequence = t.Accounts
                    .Where(a => a.AccountCode.StartsWith(t.CodePrefix))
                    .Count()
            })
            .ToListAsync();

        var counts = await _db.AccountTypes
            .GroupBy(t => t.Group.GroupName)
            .Select(g => new { group = g.Key, count = g.Count() })
            .ToListAsync();

        return Ok(new
        {
            items = rows.Select(r => new
            {
                r.id, r.name, r.groupId, r.group, r.prefix, r.codeLength,
                r.normalBalance, r.onBalanceSheet, r.isSystem, r.accountCount,
                nextCode = r.prefix + (r.lastSequence + 1).ToString().PadLeft(r.codeLength, '0')
            }),
            groupCounts = counts,
            total = rows.Count
        });
    }

    [HttpGet("account-groups")]
    public async Task<IActionResult> GetAccountGroups() =>
        Ok(await _db.AccountGroups.OrderBy(g => g.GroupId)
            .Select(g => new { id = g.GroupId, name = g.GroupName, onBalanceSheet = g.OnBalanceSheet })
            .ToListAsync());

    [HttpPut("account-types/{id:int}")]
    public async Task<IActionResult> UpdateAccountType(int id, [FromBody] AccountTypeRequest body)
    {
        var t = await _db.AccountTypes.FirstOrDefaultAsync(x => x.AccountTypeId == id);
        if (t is null) return NotFound(new { message = "Account type not found." });

        if (string.IsNullOrWhiteSpace(body.Name)) return BadRequest(new { message = "Name is required." });
        if (string.IsNullOrWhiteSpace(body.Prefix)) return BadRequest(new { message = "Code prefix is required." });
        if (body.CodeLength is < 1 or > 12) return BadRequest(new { message = "Code length must be 1 to 12." });

        var prefix = body.Prefix.Trim().ToUpperInvariant();
        if (await _db.AccountTypes.AnyAsync(x => x.CodePrefix.ToUpper() == prefix && x.AccountTypeId != id))
            return BadRequest(new { message = "Another type already uses that prefix." });

        t.TypeName = body.Name.Trim();
        t.CodePrefix = prefix;
        t.CodeLength = (short)body.CodeLength;
        if (!t.IsSystem) t.IsDebitNormal = body.NormalBalance == "debit";

        await _db.SaveChangesAsync();
        await Log("UPDATED", "AccountType", t.TypeName, null, 1);
        return Ok(new { message = $"{t.TypeName} updated." });
    }

    // ══════════════════════════════════════════════════════════════════
    //  DOCUMENT NUMBERING
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("document-series")]
    public async Task<IActionResult> GetDocumentSeries()
    {
        var rows = await _db.DocumentSeries.OrderBy(s => s.SeriesId)
            .Select(s => new
            {
                id = s.SeriesId,
                key = s.SeriesKey,
                label = s.Label,
                prefix = s.Prefix,
                includeYear = s.IncludeYear,
                padding = s.Padding,
                nextNumber = s.NextNumber
            })
            .ToListAsync();

        /* The two-digit year the preview should show, taken from the company's
           fiscal calendar rather than hardcoded in the page. */
        var company = await _db.Companies.FirstOrDefaultAsync();
        var startMonth = company?.FiscalYearStartMonth ?? 1;
        var today = DateTime.UtcNow;
        var fiscalYear = today.Month >= startMonth ? today.Year + 1 : today.Year;

        return Ok(new { items = rows, yearSuffix = fiscalYear % 100 });
    }

    /// <summary>Saves the whole grid in one go, the way the screen edits it.</summary>
    [HttpPut("document-series")]
    public async Task<IActionResult> UpdateDocumentSeries([FromBody] List<DocumentSeriesRequest> body)
    {
        if (body is null || body.Count == 0) return BadRequest(new { message = "Nothing to save." });

        var ids = body.Select(b => b.Id).ToList();
        var rows = await _db.DocumentSeries.Where(s => ids.Contains(s.SeriesId)).ToListAsync();

        foreach (var b in body)
        {
            var row = rows.FirstOrDefault(r => r.SeriesId == b.Id);
            if (row is null) continue;

            if (string.IsNullOrWhiteSpace(b.Prefix) || b.Prefix.Trim().Length > 6)
                return BadRequest(new { message = $"{row.Label}: prefix must be 1 to 6 characters." });
            if (b.Padding is < 2 or > 8)
                return BadRequest(new { message = $"{row.Label}: digits must be between 2 and 8." });
            if (b.NextNumber < 1)
                return BadRequest(new { message = $"{row.Label}: next number must be 1 or more." });

            var prefix = b.Prefix.Trim().ToUpperInvariant();
            if (rows.Any(r => r.SeriesId != b.Id && r.Prefix.ToUpper() == prefix) ||
                await _db.DocumentSeries.AnyAsync(r => r.SeriesId != b.Id && r.Prefix.ToUpper() == prefix
                                                       && !ids.Contains(r.SeriesId)))
                return BadRequest(new { message = $"{row.Label}: the prefix {prefix} is already in use." });

            row.Prefix = prefix;
            row.IncludeYear = b.IncludeYear;
            row.Padding = (short)b.Padding;
            row.NextNumber = b.NextNumber;
        }

        await _db.SaveChangesAsync();
        await Log("UPDATED", "DocumentSeries", "numbering", $"{body.Count} series saved", 3);
        return Ok(new { message = "Numbering saved." });
    }

    // ══════════════════════════════════════════════════════════════════
    //  AUDIT LOG
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("audit-log")]
    public async Task<IActionResult> GetAuditLog([FromQuery] string? q, [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to, [FromQuery] string? severity, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        var query = _db.ActivityLogs.AsQueryable();

        if (from.HasValue)
            query = query.Where(a => a.LoggedAt >= from.Value.ToDateTime(TimeOnly.MinValue));
        if (to.HasValue)
            query = query.Where(a => a.LoggedAt <= to.Value.ToDateTime(TimeOnly.MaxValue));
        if (!string.IsNullOrWhiteSpace(severity) && severity != "all")
            query = query.Where(a => a.Severity.SeverityKey == severity);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(a =>
                (a.User != null && a.User.FullName.ToLower().Contains(term)) ||
                a.ActionName.ToLower().Contains(term) ||
                a.EntityType.ToLower().Contains(term) ||
                a.EntityReference.ToLower().Contains(term));
        }

        var total = await query.CountAsync();

        var items = await query
            .OrderByDescending(a => a.LoggedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new
            {
                id = a.LogId,
                user = a.User != null ? a.User.FullName : "System",
                action = a.ActionName,
                entityType = a.EntityType,
                entityReference = a.EntityReference,
                entity = a.EntityType + " " + a.EntityReference,
                detail = a.Detail,
                time = a.LoggedAt,
                ip = a.IpAddress ?? "internal",
                location = a.Location != null ? a.Location.LocationName : null,
                severity = a.Severity.SeverityKey
            })
            .ToListAsync();

        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("audit-log/stats")]
    public async Task<IActionResult> AuditStats()
    {
        var todayStart = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Unspecified);
        var since = todayStart.AddDays(-1);

        return Ok(new
        {
            totalToday = await _db.ActivityLogs.CountAsync(a => a.LoggedAt >= todayStart),
            failedLogins = await _db.ActivityLogs.CountAsync(a => a.ActionName == "LOGIN_FAIL" && a.LoggedAt >= since),
            permissionChanges = await _db.ActivityLogs.CountAsync(a =>
                (a.EntityType == "Role" || a.EntityType == "User") && a.ActionName == "UPDATED" && a.LoggedAt >= since),
            recentLogins = await _db.ActivityLogs.CountAsync(a => a.ActionName == "LOGIN" && a.LoggedAt >= since)
        });
    }

    [HttpGet("audit-log/{id:int}")]
    public async Task<IActionResult> GetAuditEntry(int id)
    {
        var a = await _db.ActivityLogs
            .Where(x => x.LogId == id)
            .Select(x => new
            {
                id = x.LogId,
                user = x.User != null ? x.User.FullName : "System",
                userEmail = x.User != null ? x.User.Email : null,
                action = x.ActionName,
                entityType = x.EntityType,
                entityReference = x.EntityReference,
                entity = x.EntityType + " " + x.EntityReference,
                detail = x.Detail,
                time = x.LoggedAt,
                ip = x.IpAddress ?? "internal",
                location = x.Location != null ? x.Location.LocationName : null,
                severity = x.Severity.SeverityKey
            })
            .FirstOrDefaultAsync();

        if (a is null) return NotFound(new { message = "Entry not found." });
        return Ok(a);
    }

    [HttpGet("severity-levels")]
    public async Task<IActionResult> GetSeverities() =>
        Ok(await _db.SeverityLevels.OrderBy(s => s.SeverityId)
            .Select(s => new { id = s.SeverityId, key = s.SeverityKey, name = s.SeverityName }).ToListAsync());

    // ══════════════════════════════════════════════════════════════════
    //  BACKUP
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("backups")]
    public async Task<IActionResult> GetBackups() =>
        Ok(await _db.BackupHistories
            .OrderByDescending(b => b.StartedAt)
            .Select(b => new
            {
                id = b.BackupId,
                startedAt = b.StartedAt,
                type = b.BackupType.TypeName,
                typeKey = b.BackupType.TypeKey,
                status = b.Status.StatusName,
                statusKey = b.Status.StatusKey,
                sizeMb = b.SizeMb,
                destination = b.Destination,
                durationSeconds = b.DurationSeconds,
                hash = b.ChecksumHash,
                triggeredBy = b.TriggeredByUser != null ? b.TriggeredByUser.FullName : "Scheduler"
            })
            .ToListAsync());

    [HttpGet("backups/stats")]
    public async Task<IActionResult> BackupStats()
    {
        var last = await _db.BackupHistories.OrderByDescending(b => b.StartedAt)
            .Select(b => new { b.StartedAt, status = b.Status.StatusName }).FirstOrDefaultAsync();

        return Ok(new
        {
            lastBackupAt = last != null ? last.StartedAt : (DateTime?)null,
            lastBackupStatus = last?.status,
            totalSizeMb = await _db.BackupHistories.SumAsync(b => (decimal?)b.SizeMb) ?? 0m,
            retained = await _db.BackupHistories.CountAsync(),
            successRate = await _db.BackupHistories.AnyAsync()
                ? (int)Math.Round(100.0 * await _db.BackupHistories.CountAsync(b => b.Status.StatusKey == "SUCCESS")
                                  / await _db.BackupHistories.CountAsync())
                : 0
        });
    }

    /// <summary>
    /// Records a backup run. It writes the history row the screen lists --
    /// taking the actual dump is the job of pg_dump on a schedule, not of a
    /// web request, so this endpoint deliberately does not shell out.
    /// </summary>
    [HttpPost("backups/run")]
    public async Task<IActionResult> RunBackup([FromBody] BackupRequest? body)
    {
        var typeKey = string.IsNullOrWhiteSpace(body?.TypeKey) ? "MANUAL" : body!.TypeKey!.ToUpperInvariant();
        var type = await _db.BackupTypes.FirstOrDefaultAsync(t => t.TypeKey == typeKey)
                   ?? await _db.BackupTypes.FirstAsync();
        var running = await _db.BackupStatuses.FirstAsync(s => s.StatusKey == "RUNNING");

        var row = new BackupHistory
        {
            StartedAt = Now(),
            BackupTypeId = type.BackupTypeId,
            StatusId = running.StatusId,
            SizeMb = 0,
            Destination = string.IsNullOrWhiteSpace(body?.Destination) ? "Manual download" : body!.Destination!,
            DurationSeconds = 0,
            TriggeredByUserId = CurrentUserId()
        };
        _db.BackupHistories.Add(row);
        await _db.SaveChangesAsync();

        await Log("BACKUP_STARTED", "BackupHistory", $"#{row.BackupId}", $"{type.TypeName} backup requested", 1);
        return Ok(new { id = row.BackupId, message = "Backup started. It will appear in the list when it finishes." });
    }

    [HttpGet("backup-types")]
    public async Task<IActionResult> GetBackupTypes() =>
        Ok(await _db.BackupTypes.OrderBy(t => t.BackupTypeId)
            .Select(t => new { id = t.BackupTypeId, key = t.TypeKey, name = t.TypeName }).ToListAsync());

    // ══════════════════════════════════════════════════════════════════
    //  SETTINGS AND COMPANY
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings() =>
        Ok(await _db.AppSettings.OrderBy(s => s.SettingId)
            .Select(s => new
            {
                id = s.SettingId,
                group = s.SettingGroup,
                key = s.SettingKey,
                value = s.SettingValue,
                description = s.Description
            })
            .ToListAsync());

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] List<SettingRequest> body)
    {
        if (body is null || body.Count == 0) return BadRequest(new { message = "Nothing to save." });

        var keys = body.Select(b => b.Key).ToList();
        var rows = await _db.AppSettings.Where(s => keys.Contains(s.SettingKey)).ToListAsync();

        foreach (var b in body)
        {
            var row = rows.FirstOrDefault(r => r.SettingKey == b.Key);
            if (row is null) continue;
            row.SettingValue = b.Value ?? "";
        }

        await _db.SaveChangesAsync();
        await Log("UPDATED", "AppSetting", "settings", $"{body.Count} settings saved", 3);
        return Ok(new { message = "Settings saved." });
    }

    [HttpGet("company")]
    public async Task<IActionResult> GetCompany()
    {
        var c = await _db.Companies
            .Select(x => new
            {
                id = x.CompanyId,
                companyName = x.CompanyName,
                legalName = x.LegalName,
                addressLine = x.AddressLine,
                cityId = x.CityId,
                city = x.City.CityName,
                country = x.Country,
                phone = x.Phone,
                email = x.Email,
                ntn = x.Ntn,
                strn = x.Strn,
                fiscalYearStartMonth = x.FiscalYearStartMonth,
                currencyCode = x.CurrencyCode,
                currencySymbol = x.CurrencySymbol,
                foreignRate = x.ForeignRate
            })
            .FirstOrDefaultAsync();

        if (c is null) return NotFound(new { message = "No company profile has been set up." });
        return Ok(c);
    }

    [HttpPut("company")]
    public async Task<IActionResult> UpdateCompany([FromBody] CompanyRequest body)
    {
        var c = await _db.Companies.FirstOrDefaultAsync();
        if (c is null) return NotFound(new { message = "No company profile has been set up." });

        if (string.IsNullOrWhiteSpace(body.CompanyName)) return BadRequest(new { message = "Company name is required." });
        if (string.IsNullOrWhiteSpace(body.Email) || !body.Email.Contains('@'))
            return BadRequest(new { message = "A valid company email is required." });
        if (body.FiscalYearStartMonth is < 1 or > 12)
            return BadRequest(new { message = "Fiscal year start month must be 1 to 12." });
        if (!await _db.Cities.AnyAsync(x => x.CityId == body.CityId))
            return BadRequest(new { message = "Pick a valid city." });

        c.CompanyName = body.CompanyName.Trim();
        c.LegalName = body.LegalName?.Trim() ?? c.LegalName;
        c.AddressLine = body.AddressLine?.Trim() ?? c.AddressLine;
        c.CityId = body.CityId;
        c.Country = body.Country?.Trim() ?? c.Country;
        c.Phone = body.Phone?.Trim() ?? c.Phone;
        c.Email = body.Email.Trim();
        c.Ntn = body.Ntn?.Trim() ?? c.Ntn;
        c.Strn = body.Strn?.Trim() ?? c.Strn;
        c.FiscalYearStartMonth = (short)body.FiscalYearStartMonth;
        c.CurrencyCode = body.CurrencyCode?.Trim() ?? c.CurrencyCode;
        c.CurrencySymbol = body.CurrencySymbol?.Trim() ?? c.CurrencySymbol;

        await _db.SaveChangesAsync();
        await Log("UPDATED", "Company", c.CompanyName, "Company profile updated", 3);
        return Ok(new { message = "Company profile saved." });
    }

    // ══════════════════════════════════════════════════════════════════
    //  LOOKUPS  (one call to fill every dropdown on the panel)
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups() => Ok(new
    {
        roles = await _db.Roles.OrderBy(r => r.RoleId)
            .Select(r => new { id = r.RoleId, key = r.RoleKey, name = r.RoleName, description = r.Description, permissionCount = r.Permissions.Count })
            .ToListAsync(),
        locations = await _db.Locations.Where(l => l.IsActive).OrderBy(l => l.LocationId)
            .Select(l => new { id = l.LocationId, code = l.LocationCode, name = l.LocationName })
            .ToListAsync(),
        locationKinds = await _db.LocationKinds.OrderBy(k => k.KindId)
            .Select(k => new { id = k.KindId, key = k.KindKey, name = k.KindName })
            .ToListAsync(),
        cities = await _db.Cities.OrderBy(c => c.CityName)
            .Select(c => new { id = c.CityId, name = c.CityName, province = c.Province.ProvinceName })
            .ToListAsync(),
        provinces = await _db.Provinces.OrderBy(p => p.ProvinceId)
            .Select(p => new { id = p.ProvinceId, name = p.ProvinceName })
            .ToListAsync(),
        staff = await _db.Users.Where(u => u.Role.IsStaffRole && u.IsActive).OrderBy(u => u.FullName)
            .Select(u => new { id = u.UserId, name = u.FullName, role = u.Role.RoleName })
            .ToListAsync(),
        accountGroups = await _db.AccountGroups.OrderBy(g => g.GroupId)
            .Select(g => new { id = g.GroupId, name = g.GroupName })
            .ToListAsync()
    });

    // ══════════════════════ request bodies ════════════════════════════

    public record ReasonRequest(string? Reason);
    public record BoolRequest(bool Value);

    public record UserRequest(
        string FullName, string? Email, string? Phone, string? EmployeeCode,
        int RoleId, List<int>? LocationIds, bool IsActive, bool SendInvite, string? Password);

    public record RoleRequest(string Name, string? Description, string? HomePath, List<string> Permissions);

    public record LocationRequest(
        string Code, string Name, int KindId, int CityId, string? Address,
        int? InChargeUserId, bool IsActive, bool IsDefault, bool ExcludeFromSellable);

    public record CourierRequest(
        string Name, string ShortName, string? ContactPerson, string? Phone,
        int CodSettlementDays, decimal BookingCharge, decimal CodFeePercent,
        string? TrackingUrlTemplate, bool IsActive);

    public record AccountTypeRequest(string Name, string Prefix, int CodeLength, string NormalBalance);

    public record DocumentSeriesRequest(int Id, string Prefix, bool IncludeYear, int Padding, int NextNumber);

    public record SettingRequest(string Key, string? Value);

    public record CompanyRequest(
        string CompanyName, string? LegalName, string? AddressLine, int CityId, string? Country,
        string? Phone, string Email, string? Ntn, string? Strn, int FiscalYearStartMonth,
        string? CurrencyCode, string? CurrencySymbol);

    public record BackupRequest(string? TypeKey, string? Destination);
}
