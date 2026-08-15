using Airside.Core.Security;
using Airside.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Airside.Data.Seeding;

/// <summary>
/// Brings the store to a usable baseline at startup. Idempotent.
/// </summary>
/// <remarks>
/// <para>
/// Seeding runs here rather than through EF's <c>HasData</c>. <c>HasData</c>
/// bakes values into migration files, needs fixed timestamps that cannot come
/// from <see cref="TimeProvider"/>, and produces a pending-model diff on both
/// providers every time a seeded value changes — which, with two migration
/// assemblies, doubles the churn for no benefit.
/// </para>
/// <para>
/// It never creates a user. The first Super Admin is created through the
/// setup-token flow, so a default credential never exists — not even briefly, and
/// not even on a box that is unreachable for the first ten minutes.
/// </para>
/// </remarks>
public sealed class DbSeeder(AirsideDbContext db, ILogger<DbSeeder> logger)
{
    public async Task SeedAsync(StoreProvider provider, CancellationToken ct)
    {
        await SyncPermissionsAsync(ct).ConfigureAwait(false);
        await EnsureRolesAsync(ct).ConfigureAwait(false);
        await EnsureHostAsync(ct).ConfigureAwait(false);
        await EnsureSettingsAsync(provider, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reconciles the code-defined catalogue with the table: insert what is
    /// missing, mark what has gone as obsolete, delete nothing. A role may still
    /// reference a retired code, and a foreign key that vanishes during an upgrade
    /// takes that role's permission set with it.
    /// </summary>
    private async Task SyncPermissionsAsync(CancellationToken ct)
    {
        var existing = await db.Permissions.ToListAsync(ct).ConfigureAwait(false);
        var known = Permissions.All.ToHashSet(StringComparer.Ordinal);

        foreach (var code in known.Where(c => !existing.Exists(e => e.Code == c)))
        {
            db.Permissions.Add(new Permission { Code = code });
            logger.LogInformation("Registered new permission {PermissionCode}", code);
        }

        foreach (var stale in existing.Where(e => !known.Contains(e.Code) && !e.IsObsolete))
        {
            stale.IsObsolete = true;
            logger.LogWarning("Permission {PermissionCode} is no longer defined; marked obsolete", stale.Code);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task EnsureRolesAsync(CancellationToken ct)
    {
        foreach (var (slug, name, permissions) in RoleDefinitions.All)
        {
            var role = await db.Roles
                .Include(r => r.RolePermissions)
                .FirstOrDefaultAsync(r => r.Slug == slug, ct)
                .ConfigureAwait(false);

            if (role is null)
            {
                role = new Role { Slug = slug, Name = name, IsSystem = true };
                db.Roles.Add(role);

                foreach (var code in permissions)
                {
                    role.RolePermissions.Add(new RolePermission { Role = role, PermissionCode = code });
                }

                logger.LogInformation("Seeded system role {RoleSlug}", slug);
                continue;
            }

            // An existing role's permission set is left alone. Operators are
            // allowed to edit system roles other than Super Admin, and re-imposing
            // the defaults on every restart would silently undo their work.
            if (slug != SystemRoles.SuperAdmin)
            {
                continue;
            }

            // Super Admin is the exception: it must always hold everything, or an
            // upgrade that adds a permission leaves nobody able to grant it.
            foreach (var code in permissions.Where(c => !role.RolePermissions.Any(rp => rp.PermissionCode == c)))
            {
                role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionCode = code });
                logger.LogInformation("Granted {PermissionCode} to super-admin", code);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task EnsureHostAsync(CancellationToken ct)
    {
        if (await db.Hosts.AnyAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        // Capacity stays zero until the resource reader discovers it. Zero admits
        // nothing, which is the correct failure direction: a host that rejects
        // provisioning until it knows its own size is safe, whereas one that
        // guesses generously is not.
        db.Hosts.Add(new Entities.Host { Name = "local", IsLocal = true });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Seeded local host record");
    }

    private async Task EnsureSettingsAsync(StoreProvider provider, CancellationToken ct)
    {
        var settings = await db.InstanceSettings
            .FirstOrDefaultAsync(x => x.Id == Entities.InstanceSettings.SingletonId, ct)
            .ConfigureAwait(false);

        if (settings is null)
        {
            db.InstanceSettings.Add(new Entities.InstanceSettings
            {
                Id = Entities.InstanceSettings.SingletonId,
                StoreProvider = provider,
            });
        }
        else if (settings.StoreProvider != provider)
        {
            settings.StoreProvider = provider;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// The six built-in roles as permission bundles.
/// </summary>
/// <remarks>
/// The split that matters is Infrastructure Admin versus Database Admin: the
/// former can restart, resize, and delete a database but cannot read its
/// contents, and the latter can. That distinction is the whole reason
/// authorisation checks permissions rather than role names.
/// </remarks>
internal static class RoleDefinitions
{
    public static IReadOnlyList<(string Slug, string Name, IReadOnlyList<string> Permissions)> All { get; } =
    [
        (SystemRoles.SuperAdmin, "Super Admin", Permissions.All),

        (SystemRoles.InfrastructureAdmin, "Infrastructure Admin",
        [
            Permissions.ServerManage, Permissions.ServerUpdate, Permissions.AuditRead,
            Permissions.DatabaseRead, Permissions.DatabaseCreate, Permissions.DatabaseUpdate,
            Permissions.DatabaseDelete, Permissions.DatabaseLifecycle, Permissions.DatabaseBackup,
            Permissions.DatabaseRestore, Permissions.DatabaseRotateCredentials,
            Permissions.ApplicationRead, Permissions.ApplicationCreate, Permissions.ApplicationUpdate,
            Permissions.ApplicationDelete, Permissions.ApplicationLifecycle,
            Permissions.DomainRead, Permissions.DomainManage,
            Permissions.SecretView, Permissions.LogsRead, Permissions.MetricsRead,
            // Deliberately absent: database.query and secret.read. Managing the
            // box is not the same as reading what is stored on it.
        ]),

        (SystemRoles.DatabaseAdmin, "Database Admin",
        [
            Permissions.DatabaseRead, Permissions.DatabaseCreate, Permissions.DatabaseUpdate,
            Permissions.DatabaseDelete, Permissions.DatabaseLifecycle, Permissions.DatabaseBackup,
            Permissions.DatabaseRestore, Permissions.DatabaseRotateCredentials,
            Permissions.DatabaseQuery,
            Permissions.LogsRead, Permissions.MetricsRead,
            // database.query_destructive is not granted by default to anyone but
            // Super Admin. FLUSHALL and SHUTDOWN should require a deliberate act
            // of role editing, not arrive with the job title.
        ]),

        (SystemRoles.ApplicationAdmin, "Application Admin",
        [
            Permissions.ApplicationRead, Permissions.ApplicationCreate, Permissions.ApplicationUpdate,
            Permissions.ApplicationDelete, Permissions.ApplicationLifecycle, Permissions.ApplicationDeploy,
            Permissions.ApplicationRollback, Permissions.ApplicationAttachDatabase,
            Permissions.SecretView, Permissions.SecretRead, Permissions.SecretWrite,
            Permissions.DomainRead, Permissions.DomainManage,
            Permissions.DatabaseRead, Permissions.LogsRead, Permissions.MetricsRead,
        ]),

        (SystemRoles.Developer, "Developer",
        [
            Permissions.ApplicationRead, Permissions.ApplicationDeploy, Permissions.ApplicationRollback,
            Permissions.ApplicationLifecycle, Permissions.DatabaseRead,
            Permissions.SecretView, Permissions.LogsRead, Permissions.MetricsRead,
        ]),

        (SystemRoles.ReadOnly, "Read Only",
        [
            Permissions.DatabaseRead, Permissions.ApplicationRead, Permissions.DomainRead,
            Permissions.LogsRead, Permissions.MetricsRead,
        ]),
    ];
}
