using Airside.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Airside.Data.Configurations;

public class BackupConfiguration : IEntityTypeConfiguration<Backup>
{
    public void Configure(EntityTypeBuilder<Backup> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("backups");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.TriggerKind).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.StoragePath).HasMaxLength(512).IsRequired();
        builder.Property(x => x.Sha256).HasMaxLength(64);
        builder.Property(x => x.EngineSnapshot).HasMaxLength(64).IsRequired();
        builder.Property(x => x.DatabaseNameSnapshot).HasMaxLength(64);
        builder.Property(x => x.ErrorMessage).HasMaxLength(1024);

        // Restrict, not Cascade: deleting a database keeps its backups, for the
        // same reason it keeps its volume unless told otherwise. A backup is the
        // last copy of data somebody may still want.
        builder.HasOne(x => x.DatabaseInstance)
            .WithMany()
            .HasForeignKey(x => x.DatabaseInstanceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.DatabaseInstanceId, x.StartedAt });
        builder.HasIndex(x => x.ExpiresAt);
    }
}

public class RestoreConfiguration : IEntityTypeConfiguration<Restore>
{
    public void Configure(EntityTypeBuilder<Restore> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("restores");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.ErrorCode).HasMaxLength(64);
        builder.Property(x => x.ErrorMessage).HasMaxLength(1024);

        builder.HasOne(x => x.DatabaseInstance)
            .WithMany()
            .HasForeignKey(x => x.DatabaseInstanceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Backup)
            .WithMany()
            .HasForeignKey(x => x.BackupId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.DatabaseInstanceId, x.StartedAt });
    }
}

public class SavedQueryConfiguration : IEntityTypeConfiguration<SavedQuery>
{
    public void Configure(EntityTypeBuilder<SavedQuery> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("saved_queries");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Body).HasMaxLength(64 * 1024).IsRequired();
        builder.HasIndex(x => new { x.UserId, x.Name });
    }
}

public class QueryHistoryEntryConfiguration : IEntityTypeConfiguration<QueryHistoryEntry>
{
    public void Configure(EntityTypeBuilder<QueryHistoryEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("query_history");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Body).HasMaxLength(64 * 1024).IsRequired();
        builder.Property(x => x.ErrorMessage).HasMaxLength(1024);

        // Always filtered by user first: history is per-user and never listable by
        // anyone else, because the statements in it can contain literal secrets.
        builder.HasIndex(x => new { x.UserId, x.DatabaseInstanceId, x.ExecutedAt });
    }
}
