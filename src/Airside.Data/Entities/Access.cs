using Microsoft.AspNetCore.Identity;

namespace Airside.Data.Entities;

/// <summary>
/// An Airside operator.
/// </summary>
/// <remarks>
/// Built on ASP.NET Core Identity's user store. Password hashing, security
/// stamps, lockout, email normalisation, TOTP, and recovery codes are exactly
/// what "don't hand-roll crypto" means, and reimplementing security stamps
/// correctly is a poor use of a first release.
/// <para>
/// Identity's <em>role</em> system is deliberately not used — it is string-based
/// and cannot express permission bundles. <c>AirsideDbContext</c> derives from
/// <c>IdentityUserContext</c> rather than <c>IdentityDbContext</c>, so the role
/// tables are never mapped and the authorisation model below is the only one.
/// </para>
/// </remarks>
public class AirsideUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public DateTime? DeactivatedAt { get; set; }

    public bool MustChangePassword { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public ICollection<UserRole> UserRoles { get; } = new List<UserRole>();

    public ICollection<UserSession> Sessions { get; } = new List<UserSession>();
}

/// <summary>A named bundle of permissions. Never checked directly — see <see cref="Permission"/>.</summary>
public class Role : Entity
{
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>One of the six built-ins. Cannot be deleted or renamed.</summary>
    public bool IsSystem { get; set; }

    public ICollection<RolePermission> RolePermissions { get; } = new List<RolePermission>();

    public ICollection<UserRole> UserRoles { get; } = new List<UserRole>();
}

/// <summary>
/// A permission, keyed by its code.
/// </summary>
/// <remarks>
/// The primary key is the string code — <c>database.query</c> — not a surrogate
/// uuid. A natural key makes the join table readable in a raw query and keeps the
/// permission a greppable constant rather than an opaque id.
/// <para>
/// The catalogue is defined in <c>Airside.Core.Security.Permissions</c> and
/// synchronised here at startup. Codes are never deleted, only marked obsolete: a
/// role may still reference one, and a foreign key that vanishes during an
/// upgrade takes the role's permission set with it.
/// </para>
/// </remarks>
public class Permission
{
    public string Code { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsObsolete { get; set; }

    public ICollection<RolePermission> RolePermissions { get; } = new List<RolePermission>();
}

public class RolePermission
{
    public Guid RoleId { get; set; }

    public Role Role { get; set; } = null!;

    public string PermissionCode { get; set; } = string.Empty;

    public Permission Permission { get; set; } = null!;
}

public class UserRole
{
    public Guid UserId { get; set; }

    public AirsideUser User { get; set; } = null!;

    public Guid RoleId { get; set; }

    public Role Role { get; set; } = null!;

    public DateTime GrantedAt { get; set; }

    public Guid? GrantedByUserId { get; set; }
}

/// <summary>
/// A signed-in session.
/// </summary>
/// <remarks>
/// Authentication is cookie-based: the dashboard is same-origin, SignalR carries
/// the cookie natively, and an HttpOnly SameSite=Strict cookie keeps the
/// credential out of JavaScript entirely. This table exists because cookie auth
/// alone cannot express individual revocation — "sign out my other devices", or
/// dropping every session the moment a user is deactivated.
/// </remarks>
public class UserSession : Entity
{
    public Guid UserId { get; set; }

    public AirsideUser User { get; set; } = null!;

    public DateTime ExpiresAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public DateTime? RevokedAt { get; set; }

    public string? RevokedReason { get; set; }

    public bool IsActive(DateTime now) => RevokedAt is null && ExpiresAt > now;
}
