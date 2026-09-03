using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace vizo_backend.Services;

/// <summary>
/// Authorising on WHAT SOMEBODY MAY DO rather than on which role they hold.
///
/// ─────────────────────────── WHY THIS EXISTS ───────────────────────────────
///
/// The policies were hard-coded lists of roles:
///
///     AddPolicy("BackOffice", p => p.RequireRole("super-admin", "accountant", "order-dept"));
///
/// So the sales returns screens were closed to the Sales role by name. A Super
/// Admin could tick "returns.sales" for that role in Setup, watch it save, and
/// the salesperson would still get a 403 -- because the endpoint was never
/// asking about permissions at all. The permission screen was decoration.
///
/// It also meant a NEW role could not be given anything. The Warehouse Keeper
/// added for the order workflow holds no role name any of those lists mention,
/// so every endpoint in the application would have refused it.
///
/// ─────────────────────────── HOW IT WORKS ──────────────────────────────────
///
/// Write [Authorize(Policy = "perm:returns.sales")] and the policy is built on
/// demand -- there is no list of 38 policies to keep in step with the 38
/// permissions. The JWT already carries the holder's permissions as "perm"
/// claims, so no database round trip is needed either.
///
/// The Super Admin passes everything, on the same reasoning the rest of the app
/// uses: they hold every permission by definition, and locking the owner out of
/// their own system because a row is missing helps nobody.
/// </summary>
public class PermissionRequirement : IAuthorizationRequirement
{
    public string Permission { get; }
    public PermissionRequirement(string permission) => Permission = permission;
}

public class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    public const string ClaimType = "perm";
    public const string Prefix = "perm:";

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var user = context.User;

        if (user.IsInRole("super-admin"))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var has = user.Claims.Any(c => c.Type == ClaimType && c.Value == requirement.Permission);
        if (has) context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Builds a "perm:xxx" policy the first time one is asked for, and falls back to
/// the normal provider for every policy declared in Program.cs.
/// </summary>
public class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
        => _fallback = new DefaultAuthorizationPolicyProvider(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(PermissionHandler.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            var permission = policyName[PermissionHandler.Prefix.Length..];
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission))
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return _fallback.GetPolicyAsync(policyName);
    }
}
