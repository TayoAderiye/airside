using Airside.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Airside.Data.Configurations;

public class RegistryCredentialConfiguration : IEntityTypeConfiguration<RegistryCredential>
{
    public void Configure(EntityTypeBuilder<RegistryCredential> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("registry_credentials");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Registry).HasMaxLength(253).IsRequired();
        builder.Property(x => x.Username).HasMaxLength(256).IsRequired();
        builder.Property(x => x.EncryptedPassword).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(128);
        builder.Property(x => x.LastVerificationError).HasMaxLength(512);

        // One credential per registry. Two would make which one is used depend on
        // query order, and a pull failing because it picked the stale one is not
        // something anyone would think to look for.
        builder.HasIndex(x => x.Registry).IsUnique();

        builder.HasQueryFilter(x => x.DeletedAt == null);
    }
}
