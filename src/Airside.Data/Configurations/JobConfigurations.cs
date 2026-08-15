using Airside.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Airside.Data.Configurations;

public class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("jobs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Type).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.CurrentStep).HasMaxLength(256);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(256).IsRequired();
        builder.Property(x => x.LeaseOwner).HasMaxLength(128);
        builder.Property(x => x.ErrorCode).HasMaxLength(64);
        builder.Property(x => x.ErrorMessage).HasMaxLength(1024);
        builder.Property(x => x.CorrelationId).HasMaxLength(64);

        // Deduplicates a double-clicked button into one job rather than two
        // containers. Unique across all jobs, not just in-flight ones, so a
        // completed provision cannot be replayed into a duplicate.
        builder.HasIndex(x => x.IdempotencyKey).IsUnique();

        // The dispatcher's hot query: the next queued job, oldest first.
        builder.HasIndex(x => new { x.Status, x.QueuedAt });
        builder.HasIndex(x => x.WorkloadId);
    }
}

public class JobStepConfiguration : IEntityTypeConfiguration<JobStep>
{
    public void Configure(EntityTypeBuilder<JobStep> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("job_steps");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Message).HasMaxLength(4096);

        builder.HasOne(x => x.Job)
            .WithMany(x => x.Steps)
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.JobId, x.Sequence }).IsUnique();
    }
}

public class JobResourceConfiguration : IEntityTypeConfiguration<JobResource>
{
    public void Configure(EntityTypeBuilder<JobResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("job_resources");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.Reference).HasMaxLength(256).IsRequired();

        builder.HasOne(x => x.Job)
            .WithMany(x => x.Resources)
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.JobId, x.CompensatedAt });
    }
}

public class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("audit_events");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Action).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Result).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.UserEmailSnapshot).HasMaxLength(256);
        builder.Property(x => x.ResourceKind).HasMaxLength(64);
        builder.Property(x => x.ResourceSlugSnapshot).HasMaxLength(64);
        builder.Property(x => x.IpAddress).HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasMaxLength(512);
        builder.Property(x => x.CorrelationId).HasMaxLength(64);

        // No navigation to AirsideUser, deliberately. A foreign key here would
        // either block deleting a user or cascade away their history; the email
        // snapshot is what keeps the record readable once the user is gone.

        // Keyset pagination over an append-only log: offset paging silently skips
        // rows as new ones arrive, and audit is where that matters most.
        builder.HasIndex(x => new { x.OccurredAt, x.Id });
        builder.HasIndex(x => x.Action);
        builder.HasIndex(x => x.ResourceId);
    }
}
