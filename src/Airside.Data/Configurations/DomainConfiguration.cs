using Airside.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Airside.Data.Configurations;

public class DomainConfiguration : IEntityTypeConfiguration<Domain>
{
    public void Configure(EntityTypeBuilder<Domain> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("domains");
        builder.HasKey(x => x.Id);

        // 253 is the maximum length of a DNS name.
        builder.Property(x => x.Hostname).HasMaxLength(253).IsRequired();
        builder.Property(x => x.DisplayHostname).HasMaxLength(253).IsRequired();
        builder.Property(x => x.RegisteredDomain).HasMaxLength(253).IsRequired();
        builder.Property(x => x.TlsMode).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.RouteId).HasMaxLength(128);
        builder.Property(x => x.CertificateIssuer).HasMaxLength(256);
        builder.Property(x => x.CertificateSubject).HasMaxLength(256);
        builder.Property(x => x.CertificateFingerprint).HasMaxLength(128);
        builder.Property(x => x.ErrorCode).HasMaxLength(64);
        builder.Property(x => x.ErrorMessage).HasMaxLength(1024);

        builder.HasOne(x => x.Application)
            .WithMany()
            .HasForeignKey(x => x.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Self-reference for apex/www redirects. Restricted rather than cascading:
        // deleting the target of a redirect should be refused with an explanation,
        // not silently take the redirecting hostname down with it.
        builder.HasOne<Domain>()
            .WithMany()
            .HasForeignKey(x => x.RedirectToDomainId)
            .OnDelete(DeleteBehavior.Restrict);

        // One hostname routes to one application. Two routes matching the same
        // host would make which one wins depend on insertion order.
        builder.HasIndex(x => x.Hostname).IsUnique();

        // Rate-limit accounting groups by registered domain, and the expiry sweep
        // scans by NotAfter across every row.
        builder.HasIndex(x => x.RegisteredDomain);
        builder.HasIndex(x => x.CertificateNotAfter);

        builder.HasQueryFilter(x => x.DeletedAt == null);
    }
}

public class IssuanceAttemptConfiguration : IEntityTypeConfiguration<IssuanceAttempt>
{
    public void Configure(EntityTypeBuilder<IssuanceAttempt> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("issuance_attempts");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Hostname).HasMaxLength(253).IsRequired();
        builder.Property(x => x.RegisteredDomain).HasMaxLength(253).IsRequired();
        builder.Property(x => x.ErrorCode).HasMaxLength(64);

        // Every rate-limit question is "how many, for this name, since when", so
        // both counting indexes lead with the grouping column and end with time.
        builder.HasIndex(x => new { x.RegisteredDomain, x.AttemptedAt });
        builder.HasIndex(x => new { x.Hostname, x.AttemptedAt });
    }
}

public class DomainCertificateConfiguration : IEntityTypeConfiguration<DomainCertificate>
{
    public void Configure(EntityTypeBuilder<DomainCertificate> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("domain_certificates");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ChainPem).IsRequired();
        builder.Property(x => x.EncryptedPrivateKey).IsRequired();
        builder.Property(x => x.Fingerprint).HasMaxLength(128);

        // One current certificate per domain. A replacement overwrites rather than
        // accumulating, so there is never a question of which one is being served.
        builder.HasIndex(x => x.DomainId).IsUnique();
    }
}
