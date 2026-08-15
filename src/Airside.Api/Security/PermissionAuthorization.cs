using System.Security.Claims;
using Airside.Core.Security;
using Airside.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Airside.Api.Security;

/// <summary>The claim every permission check reads.</summary>
public static class AirsideClaims
{
    public const string Permission = "airside:permission";
    public const string SessionId = "airside:session";
}

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Grants access when the caller holds the required permission.
/// </summary>
/// <remarks>
/// Checks a permission, never a role name. That distinction is the whole point of
/// roles being bundles: an operator can build a role that restarts databases
/// without reading them, and no code needs to change for it to work.
/// </remarks>
public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var granted = context.User.Claims.Any(c =>
            string.Equals(c.Type, AirsideClaims.Permission, StringComparison.Ordinal)
            && string.Equals(c.Value, requirement.Permission, StringComparison.Ordinal));

        if (granted)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Resolves policies by permission name so every permission does not need an
/// explicit registration.
/// </summary>
/// <remarks>
/// The provider validates against the compile-time catalogue rather than accepting
/// any string: a typo in <c>RequirePermission("databse.create")</c> would
/// otherwise produce a policy nobody holds, and the endpoint would fail closed but
/// silently — which looks exactly like a permissions bug in production.
/// </remarks>
public sealed class PermissionPolicyProvider(
    Microsoft.Extensions.Options.IOptions<AuthorizationOptions> options)
    : DefaultAuthorizationPolicyProvider(options)
{
    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!Permissions.All.Contains(policyName, StringComparer.Ordinal))
        {
            return base.GetPolicyAsync(policyName);
        }

        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(policyName))
            .Build();

        return Task.FromResult<AuthorizationPolicy?>(policy);
    }
}

public static class PermissionEndpointExtensions
{
    /// <summary>
    /// Requires a permission on an endpoint. Throws at startup if the permission
    /// is not in the catalogue, so a typo fails the build-out rather than
    /// producing an endpoint nobody can reach.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        if (!Permissions.All.Contains(permission, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"'{permission}' is not a known permission. Add it to Airside.Core.Security.Permissions.",
                nameof(permission));
        }

        return builder.RequireAuthorization(permission);
    }
}

/// <summary>Builds the claims for a signed-in user from their roles.</summary>
public sealed class ClaimsFactory(AirsideDbContext db)
{
    public async Task<IReadOnlyList<Claim>> BuildAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var permissions = await db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => rp.PermissionCode)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(AirsideClaims.SessionId, sessionId.ToString()),
        };

        claims.AddRange(permissions.Select(p => new Claim(AirsideClaims.Permission, p)));
        return claims;
    }
}
