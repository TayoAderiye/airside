namespace Airside.Core.Security;

/// <summary>
/// Every permission Airside checks.
/// </summary>
/// <remarks>
/// <para>
/// Roles are bundles of these. Authorisation always checks a permission, never a
/// role name — the brief's own example is the reason: a user may legitimately be
/// allowed to restart a database but not to read its contents, and that is
/// inexpressible if the check asks "are you a Database Admin?".
/// </para>
/// <para>
/// The catalogue is defined here and synchronised into the store at startup:
/// missing codes are inserted, absent ones are marked obsolete and never deleted,
/// because a role may still reference one. Adding a permission is a constant here
/// plus a seeder run — not a migration on two providers.
/// </para>
/// <para>
/// Permissions are global, not resource-scoped: <c>database.query</c> grants
/// query access to every database, not a chosen subset. That is a deliberate MVP
/// limitation recorded in DATA-MODEL.md §4, not an oversight.
/// </para>
/// </remarks>
public static class Permissions
{
    public const string ServerManage = "server.manage";
    public const string ServerUpdate = "server.update";

    public const string UserManage = "user.manage";
    public const string RoleManage = "role.manage";
    public const string AuditRead = "audit.read";

    public const string DatabaseRead = "database.read";
    public const string DatabaseCreate = "database.create";
    public const string DatabaseUpdate = "database.update";
    public const string DatabaseDelete = "database.delete";
    public const string DatabaseLifecycle = "database.lifecycle";
    public const string DatabaseBackup = "database.backup";
    public const string DatabaseRestore = "database.restore";
    public const string DatabaseRotateCredentials = "database.rotate_credentials";

    /// <summary>Deliberately independent of every other database permission.</summary>
    public const string DatabaseQuery = "database.query";

    /// <summary>
    /// Gates the commands that take a production instance down — <c>FLUSHALL</c>,
    /// <c>CONFIG SET</c>, <c>SHUTDOWN</c>, <c>KEYS</c> on a large keyspace.
    /// </summary>
    public const string DatabaseQueryDestructive = "database.query_destructive";

    public const string ApplicationRead = "application.read";
    public const string ApplicationCreate = "application.create";
    public const string ApplicationUpdate = "application.update";
    public const string ApplicationDelete = "application.delete";
    public const string ApplicationLifecycle = "application.lifecycle";
    public const string ApplicationDeploy = "application.deploy";
    public const string ApplicationRollback = "application.rollback";
    public const string ApplicationAttachDatabase = "application.attach_database";

    public const string SecretRead = "secret.read";
    public const string SecretWrite = "secret.write";

    /// <summary>Separate from <see cref="SecretRead"/>: listing keys is not reading values.</summary>
    public const string SecretView = "secret.view";

    public const string DomainRead = "domain.read";
    public const string DomainManage = "domain.manage";

    public const string LogsRead = "logs.read";
    public const string MetricsRead = "metrics.read";

    public static IReadOnlyList<string> All { get; } =
    [
        ServerManage, ServerUpdate,
        UserManage, RoleManage, AuditRead,
        DatabaseRead, DatabaseCreate, DatabaseUpdate, DatabaseDelete, DatabaseLifecycle,
        DatabaseBackup, DatabaseRestore, DatabaseRotateCredentials,
        DatabaseQuery, DatabaseQueryDestructive,
        ApplicationRead, ApplicationCreate, ApplicationUpdate, ApplicationDelete,
        ApplicationLifecycle, ApplicationDeploy, ApplicationRollback, ApplicationAttachDatabase,
        SecretRead, SecretWrite, SecretView,
        DomainRead, DomainManage,
        LogsRead, MetricsRead,
    ];
}

/// <summary>The six built-in roles. Bundles only — the checks are always on permissions.</summary>
public static class SystemRoles
{
    public const string SuperAdmin = "super-admin";
    public const string InfrastructureAdmin = "infrastructure-admin";
    public const string DatabaseAdmin = "database-admin";
    public const string ApplicationAdmin = "application-admin";
    public const string Developer = "developer";
    public const string ReadOnly = "read-only";

    public static IReadOnlyList<string> All { get; } =
    [
        SuperAdmin, InfrastructureAdmin, DatabaseAdmin, ApplicationAdmin, Developer, ReadOnly,
    ];
}
