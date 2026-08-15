using Airside.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Airside.Data.Configurations;

public class WorkloadConfiguration : IEntityTypeConfiguration<Workload>
{
    public void Configure(EntityTypeBuilder<Workload> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("workloads");
        builder.HasKey(x => x.Id);

        // TPH. Application arrives as a second subtype in Phase 4; adding it then
        // is additive nullable columns, which the expand-then-contract rule allows.
        builder.HasDiscriminator(x => x.Kind)
            .HasValue<DatabaseInstance>(Core.Workloads.WorkloadKind.Database);

        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.Slug).HasMaxLength(32).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.State).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ContainerId).HasMaxLength(128);
        builder.Property(x => x.NetworkId).HasMaxLength(128);
        builder.Property(x => x.NetworkName).HasMaxLength(128);
        builder.Property(x => x.DriftState).HasConversion<string>().HasMaxLength(32);

        // Unique among non-deleted rows only, so a slug can be reused after a
        // delete. History stays unambiguous because audit, backup, and deployment
        // rows carry both the workload id and a slug snapshot.
        builder.HasIndex(x => new { x.HostId, x.Slug })
            .IsUnique()
            .HasFilter(null);

        builder.HasOne(x => x.Host)
            .WithMany()
            .HasForeignKey(x => x.HostId)
            .OnDelete(DeleteBehavior.Restrict);

        // Soft delete: the states include Deleted and audit references must not
        // dangle. Reads that need deleted rows say IgnoreQueryFilters explicitly.
        builder.HasQueryFilter(x => x.DeletedAt == null);

        builder.HasIndex(x => x.DeletedAt);
    }
}

public class DatabaseInstanceConfiguration : IEntityTypeConfiguration<DatabaseInstance>
{
    public void Configure(EntityTypeBuilder<DatabaseInstance> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Property(x => x.Engine).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.Version).HasMaxLength(32);
        builder.Property(x => x.ImageRef).HasMaxLength(256);
        builder.Property(x => x.ImageDigest).HasMaxLength(128);
        builder.Property(x => x.DatabaseName).HasMaxLength(64);
        builder.Property(x => x.PublishBindAddress).HasMaxLength(64);
        builder.Property(x => x.MaxMemoryPolicy).HasMaxLength(32);
        builder.Property(x => x.BackupCron).HasMaxLength(64);
    }
}

public class DatabaseCredentialConfiguration : IEntityTypeConfiguration<DatabaseCredential>
{
    public void Configure(EntityTypeBuilder<DatabaseCredential> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("database_credentials");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Username).HasMaxLength(64);
        builder.Property(x => x.EncryptedPassword).IsRequired();
        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(32);

        builder.HasOne(x => x.DatabaseInstance)
            .WithMany(x => x.Credentials)
            .HasForeignKey(x => x.DatabaseInstanceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.DatabaseInstanceId, x.State });
    }
}

public class VolumeConfiguration : IEntityTypeConfiguration<Volume>
{
    public void Configure(EntityTypeBuilder<Volume> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("volumes");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(128).IsRequired();
        builder.Property(x => x.MountPath).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(32);

        // Restrict, not Cascade. Deleting a database must not delete its data
        // unless the admin explicitly opted in, and a cascade here would quietly
        // make that promise unkeepable.
        builder.HasOne(x => x.Workload)
            .WithMany(x => x.Volumes)
            .HasForeignKey(x => x.WorkloadId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.Name).IsUnique();
        builder.HasIndex(x => x.OrphanedAt);
        builder.HasQueryFilter(x => x.DeletedAt == null);
    }
}
