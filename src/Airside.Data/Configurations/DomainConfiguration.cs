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
        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.RouteId).HasMaxLength(128);
        builder.Property(x => x.CertificateIssuer).HasMaxLength(256);
        builder.Property(x => x.ErrorCode).HasMaxLength(64);

        builder.HasOne(x => x.Application)
            .WithMany()
            .HasForeignKey(x => x.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        // One hostname routes to one application. Two routes matching the same
        // host would make which one wins depend on insertion order.
        builder.HasIndex(x => x.Hostname).IsUnique();
        builder.HasQueryFilter(x => x.DeletedAt == null);
    }
}
