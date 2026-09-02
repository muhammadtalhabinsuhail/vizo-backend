using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers.Admin;

/// <summary>
/// Roles and the permission matrix behind them.
///
/// Controller-only by design: no DTO classes, no services, no interfaces, no
/// repositories. Request bodies bind to the records at the foot of the file and
/// responses are anonymous objects shaped to match exactly what the screen
/// renders.
///
/// Every action is wrapped in try/catch and reports through Fail(), so a failure
/// reaches the browser as JSON with the real exception message instead of an
/// empty 500. See AdminControllerBase.
/// </summary>
[Route("api/admin")]
[ApiController]
[Authorize(Policy = "SuperAdmin")]
public class AdminRolesController : AdminControllerBase
{
    private readonly PushNotificationService _push;

    public AdminRolesController(AppDbContext db, IConfiguration cfg, ILogger<AdminRolesController> logger,
        IWebHostEnvironment env, PushNotificationService push)
        : base(db, cfg, logger, env) => _push = push;


    // ══════════════════════════════════════════════════════════════════
    //  ROLES AND PERMISSIONS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles()
    {
        try
        {
            return Ok(await _db.Roles
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
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/roles");
        }
    }

    [HttpGet("roles/{id:int}")]
    public async Task<IActionResult> GetRole(int id)
    {
        try
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
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/roles/{id:int}");
        }
    }

    /// <summary>The one permission catalogue, grouped the way the editor
    /// renders it.</summary>
    [HttpGet("permissions")]
    public async Task<IActionResult> GetPermissions()
    {
        try
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
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/permissions");
        }
    }

    [HttpPost("roles")]
    public async Task<IActionResult> CreateRole([FromBody] RoleRequest body)
    {
        try
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

            _db.Roles.Add(role);
            await _db.SaveChangesAsync();
            await Log("CREATED", "Role", role.RoleName, $"{perms.Count} permissions", 1);

            return Ok(new { id = role.RoleId, message = $"{role.RoleName} created." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/admin/roles");
        }
    }

    [HttpPut("roles/{id:int}")]
    public async Task<IActionResult> UpdateRole(int id, [FromBody] RoleRequest body)
    {
        try
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

            /* -- F2 -- ALWAYS sent, and severe. Changing what a role may do is
               the most consequential action in the whole application: it can
               hand somebody the ability to approve their own expenses. */
            await _push.NotifyRoleAsync(
                "super-admin",
                NotificationKinds.RoleChanged,
                $"Permissions changed by {CurrentUserName()}",
                $"{role.RoleName} now has {perms.Count} " +
                $"{(perms.Count == 1 ? "permission" : "permissions")}.",
                url: "/admin/roles",
                severe: true);

            return Ok(new { message = $"{role.RoleName} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/admin/roles/{id:int}");
        }
    }

    [HttpDelete("roles/{id:int}")]
    public async Task<IActionResult> DeleteRole(int id, [FromBody] ReasonRequest body)
    {
        try
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
        catch (Exception ex)
        {
            return Fail(ex, "delete /api/admin/roles/{id:int}");
        }
    }

    // ════════════════════ validation helpers ════════════════════

    private static string? ValidateRole(RoleRequest b)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length < 2) return "Role name is required.";
        if (b.Permissions is null || b.Permissions.Count == 0) return "Pick at least one permission.";
        return null;
    }

    // ══════════════════════ request bodies ══════════════════════

    public record RoleRequest(string Name, string? Description, string? HomePath, List<string> Permissions);

    // ══════════════════════ request bodies ════════════════════════════

    public record ReasonRequest(string? Reason);
}