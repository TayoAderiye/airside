using Airside.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Airside.Data;

/// <summary>
/// The control-plane store.
/// </summary>
/// <remarks>
/// Derives from <see cref="IdentityUserContext{TUser, TKey}"/> rather than
/// <c>IdentityDbContext</c>: that gives Identity's user, claim, login, and token
/// tables — the security machinery worth reusing — without its role tables, which
/// are string-based and cannot express permission bundles.
/// </remarks>
public class AirsideDbContext(DbContextOptions<AirsideDbContext> options, TimeProvider timeProvider)
    : IdentityUserContext<AirsideUser, Guid>(options)
{
    private readonly TimeProvider _timeProvider = timeProvider;

    public DbSet<Host> Hosts => Set<Host>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<UserRole> UserRoles => Set<UserRole>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<JobStep> JobSteps => Set<JobStep>();

    public DbSet<JobResource> JobResources => Set<JobResource>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<InstanceSettings> InstanceSettings => Set<InstanceSettings>();

    public DbSet<Workload> Workloads => Set<Workload>();

    public DbSet<DatabaseInstance> Databases => Set<DatabaseInstance>();

    public DbSet<DatabaseCredential> DatabaseCredentials => Set<DatabaseCredential>();

    public DbSet<Volume> Volumes => Set<Volume>();

    public DbSet<Application> Applications => Set<Application>();

    public DbSet<Deployment> Deployments => Set<Deployment>();

    public DbSet<DeploymentLog> DeploymentLogs => Set<DeploymentLog>();

    public DbSet<EnvironmentVariable> EnvironmentVariables => Set<EnvironmentVariable>();

    public DbSet<DatabaseAttachment> DatabaseAttachments => Set<DatabaseAttachment>();

    public DbSet<SourceCredential> SourceCredentials => Set<SourceCredential>();

    public DbSet<Domain> Domains => Set<Domain>();

    public DbSet<Backup> Backups => Set<Backup>();

    public DbSet<Restore> Restores => Set<Restore>();

    public DbSet<SavedQuery> SavedQueries => Set<SavedQuery>();

    public DbSet<QueryHistoryEntry> QueryHistory => Set<QueryHistoryEntry>();

    /// <summary>
    /// Every persisted timestamp is a UTC <see cref="DateTime"/>, not a
    /// <see cref="DateTimeOffset"/>.
    /// </summary>
    /// <remarks>
    /// SQLite cannot ORDER BY a DateTimeOffset at all — EF throws rather than
    /// degrading — which would break the job dispatcher and every audit query on
    /// one of the two supported providers while the other worked perfectly. Since
    /// Airside stores nothing but UTC anyway, the offset carried no information.
    /// The converter re-stamps the Kind on read, because SQLite hands back
    /// Unspecified and a serialised timestamp without a Z is a contract violation.
    /// </remarks>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcNullableDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AirsideDbContext).Assembly);

        // Identity's own tables get Airside-consistent names rather than AspNet*.
        // IdentityUserContext maps four tables; the role tables are absent by
        // construction, which is the point of using it over IdentityDbContext.
        builder.Entity<AirsideUser>().ToTable("users");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");

        // TOTP authenticator keys and recovery codes live here when MFA ships.
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyStamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ApplyStamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ApplyStamps()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        foreach (var entry in ChangeTracker.Entries())
        {
            RejectAuditMutation(entry);
            RejectImageVariantChange(entry);

            if (entry.Entity is not Entity entity)
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    entity.CreatedAt = now;
                    entity.UpdatedAt = now;
                    break;
                case EntityState.Modified:
                    entity.UpdatedAt = now;
                    // Application-managed rather than a Postgres xmin, because
                    // xmin does not exist on SQLite and Airside supports both.
                    entity.RowVersion = Guid.CreateVersion7();
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Backstop for image-variant immutability.
    /// </summary>
    /// <remarks>
    /// The service layer rejects this with a proper error; this is the guard that
    /// still holds if a future endpoint forgets to ask. Alpine and Debian builds
    /// differ in libc and in the layout an engine initialises into its volume, so
    /// flipping the variant of a provisioned database points a different build at
    /// data it did not create.
    /// </remarks>
    private static void RejectImageVariantChange(EntityEntry entry)
    {
        if (entry.Entity is not DatabaseInstance || entry.State != EntityState.Modified)
        {
            return;
        }

        foreach (var property in new[] { nameof(DatabaseInstance.ImageVariant), nameof(DatabaseInstance.UsesCustomImage) })
        {
            if (entry.Property(property).IsModified)
            {
                throw new InvalidOperationException(
                    $"{property} is fixed at provisioning and cannot be changed. Create a new database "
                    + "on the wanted variant and restore into it.");
            }
        }
    }

    /// <summary>
    /// The in-code half of append-only audit. The other half is a database-level
    /// guard in the provider-specific migration, because a rule that lives only in
    /// the application is one raw SQL statement away from not existing.
    /// </summary>
    private static void RejectAuditMutation(EntityEntry entry)
    {
        if (entry.Entity is AuditEvent && entry.State is EntityState.Modified or EntityState.Deleted)
        {
            throw new InvalidOperationException(
                "Audit events are append-only; they cannot be modified or deleted.");
        }
    }
}

internal sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

internal sealed class UtcNullableDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v == null ? null : (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()),
    v => v == null ? null : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
