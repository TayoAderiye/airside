using Airside.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Airside.Data.Configurations;

public class ApplicationConfiguration : IEntityTypeConfiguration<Application>
{
    public void Configure(EntityTypeBuilder<Application> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Property(x => x.SourceKind).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.SourceImageRef).HasMaxLength(512);
        builder.Property(x => x.GitRepositoryUrl).HasMaxLength(512);
        builder.Property(x => x.GitBranch).HasMaxLength(256);
        builder.Property(x => x.DockerfilePath).HasMaxLength(512);
        builder.Property(x => x.BuildContextPath).HasMaxLength(512);
        builder.Property(x => x.HealthCheckKind).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.HealthCheckPath).HasMaxLength(512);
    }
}

public class DeploymentConfiguration : IEntityTypeConfiguration<Deployment>
{
    public void Configure(EntityTypeBuilder<Deployment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("deployments");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.TriggerKind).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.SourceKindSnapshot).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.CommitSha).HasMaxLength(64);
        builder.Property(x => x.CommitMessage).HasMaxLength(1024);
        builder.Property(x => x.Branch).HasMaxLength(256);
        builder.Property(x => x.ImageRef).HasMaxLength(512);
        builder.Property(x => x.ImageDigest).HasMaxLength(256);
        builder.Property(x => x.ContainerId).HasMaxLength(128);
        builder.Property(x => x.ErrorCode).HasMaxLength(64);
        builder.Property(x => x.ErrorMessage).HasMaxLength(1024);

        builder.HasOne(x => x.Application)
            .WithMany(x => x.Deployments)
            .HasForeignKey(x => x.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ApplicationId, x.Number }).IsUnique();
        builder.HasIndex(x => new { x.ApplicationId, x.StartedAt });
    }
}

public class DeploymentLogConfiguration : IEntityTypeConfiguration<DeploymentLog>
{
    public void Configure(EntityTypeBuilder<DeploymentLog> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("deployment_logs");
        builder.HasKey(x => x.DeploymentId);

        builder.HasOne(x => x.Deployment)
            .WithOne(x => x.Log)
            .HasForeignKey<DeploymentLog>(x => x.DeploymentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class EnvironmentVariableConfiguration : IEntityTypeConfiguration<EnvironmentVariable>
{
    public void Configure(EntityTypeBuilder<EnvironmentVariable> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("environment_variables");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Key).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Value).HasMaxLength(32 * 1024).IsRequired();

        builder.HasOne(x => x.Application)
            .WithMany(x => x.EnvironmentVariables)
            .HasForeignKey(x => x.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ApplicationId, x.Key }).IsUnique();
    }
}

public class DatabaseAttachmentConfiguration : IEntityTypeConfiguration<DatabaseAttachment>
{
    public void Configure(EntityTypeBuilder<DatabaseAttachment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("database_attachments");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.EnvKeyPrefix).HasMaxLength(32).IsRequired();

        builder.HasOne(x => x.Application)
            .WithMany(x => x.Attachments)
            .HasForeignKey(x => x.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict: a database that an application still references cannot be
        // hard-deleted out from under it. Workloads are soft-deleted anyway, so
        // the attachment history survives either way.
        builder.HasOne(x => x.DatabaseInstance)
            .WithMany()
            .HasForeignKey(x => x.DatabaseInstanceId)
            .OnDelete(DeleteBehavior.Restrict);

        // Uniqueness is over live attachments only — the same pair may be
        // attached, detached, and attached again, and each is its own record.
        builder.HasIndex(x => new { x.ApplicationId, x.DatabaseInstanceId, x.DetachedAt });
        builder.HasIndex(x => new { x.ApplicationId, x.EnvKeyPrefix, x.DetachedAt });
    }
}

public class SourceCredentialConfiguration : IEntityTypeConfiguration<SourceCredential>
{
    public void Configure(EntityTypeBuilder<SourceCredential> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("source_credentials");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Username).HasMaxLength(128);
        builder.Property(x => x.EncryptedSecret).IsRequired();

        builder.HasIndex(x => x.Name).IsUnique();
    }
}
