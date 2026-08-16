using Airside.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Airside.Data.Configurations;

public class MetricRollupConfiguration : IEntityTypeConfiguration<MetricRollup>
{
    public void Configure(EntityTypeBuilder<MetricRollup> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("metric_rollups");
        builder.HasKey(x => x.Id);

        // One row per workload per hour, enforced rather than assumed: the roller
        // runs on a timer and a restart mid-hour must update the existing row, not
        // add a second one that halves every average.
        builder.HasIndex(x => new { x.WorkloadId, x.HourUtc }).IsUnique();

        // Charts read a window for one workload, newest first.
        builder.HasIndex(x => x.HourUtc);
    }
}

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("notifications");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.DedupeKey).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Title).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Body).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.Code).HasMaxLength(64);
        builder.Property(x => x.ResourceKind).HasMaxLength(32);
        builder.Property(x => x.Severity).HasConversion<string>().HasMaxLength(16);

        // The dedupe lookup, and the reason it is cheap enough to do on every
        // observation. Filtered to unresolved rows would be better on Postgres but
        // is not portable to SQLite, so the whole key is indexed instead.
        builder.HasIndex(x => new { x.DedupeKey, x.ResolvedAt });
        builder.HasIndex(x => x.LastSeenAt);
    }
}

public class UpdateRecordConfiguration : IEntityTypeConfiguration<UpdateRecord>
{
    public void Configure(EntityTypeBuilder<UpdateRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("update_records");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.FromVersion).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ToVersion).HasMaxLength(64).IsRequired();
        builder.Property(x => x.FromImageDigest).HasMaxLength(128);
        builder.Property(x => x.ToImageDigest).HasMaxLength(128);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.ErrorCode).HasMaxLength(64);
        builder.Property(x => x.ErrorMessage).HasMaxLength(1024);
        builder.Property(x => x.PreUpdateBackupPath).HasMaxLength(512);

        builder.HasIndex(x => x.StartedAt);
    }
}

public class UserMfaConfiguration : IEntityTypeConfiguration<UserMfa>
{
    public void Configure(EntityTypeBuilder<UserMfa> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("user_mfa");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.EncryptedSecret).IsRequired();
        builder.Property(x => x.RecoveryCodeHashes).IsRequired();

        // One enrolment per user. A second would leave two valid authenticators
        // with nothing recording which is current.
        builder.HasIndex(x => x.UserId).IsUnique();
    }
}
