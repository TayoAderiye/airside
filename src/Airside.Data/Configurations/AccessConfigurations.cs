using Airside.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Airside.Data.Configurations;

public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("roles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Slug).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(512);
        builder.HasIndex(x => x.Slug).IsUnique();
    }
}

public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("permissions");

        // The code is the key. A natural key keeps the join table readable in a
        // raw query and the permission a greppable constant, not an opaque id.
        builder.HasKey(x => x.Code);
        builder.Property(x => x.Code).HasMaxLength(64);
        builder.Property(x => x.Description).HasMaxLength(512);
    }
}

public class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("role_permissions");
        builder.HasKey(x => new { x.RoleId, x.PermissionCode });

        builder.HasOne(x => x.Role)
            .WithMany(x => x.RolePermissions)
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, not Cascade: a permission is never deleted, only marked
        // obsolete, precisely so an upgrade cannot silently empty a role's set.
        builder.HasOne(x => x.Permission)
            .WithMany(x => x.RolePermissions)
            .HasForeignKey(x => x.PermissionCode)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("user_roles");
        builder.HasKey(x => new { x.UserId, x.RoleId });

        builder.HasOne(x => x.User)
            .WithMany(x => x.UserRoles)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Role)
            .WithMany(x => x.UserRoles)
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("user_sessions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.IpAddress).HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasMaxLength(512);
        builder.Property(x => x.RevokedReason).HasMaxLength(256);

        builder.HasOne(x => x.User)
            .WithMany(x => x.Sessions)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.UserId, x.ExpiresAt });
    }
}

public class HostConfiguration : IEntityTypeConfiguration<Host>
{
    public void Configure(EntityTypeBuilder<Host> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("hosts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(128).IsRequired();
        builder.Property(x => x.VolumeRoot).HasMaxLength(512).IsRequired();
        builder.Property(x => x.DockerApiVersion).HasMaxLength(32);
        builder.Property(x => x.KernelVersion).HasMaxLength(128);
        builder.Property(x => x.OperatingSystem).HasMaxLength(128);

        // Enums are stored as strings. An enum persisted as an ordinal silently
        // corrupts every existing row the moment somebody reorders the members.
        builder.Property(x => x.StorageEnforcement).HasConversion<string>().HasMaxLength(32);
    }
}

public class InstanceSettingsConfiguration : IEntityTypeConfiguration<InstanceSettings>
{
    public void Configure(EntityTypeBuilder<InstanceSettings> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("instance_settings");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.InstanceName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.DashboardDomain).HasMaxLength(253);
        builder.Property(x => x.StoreProvider).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.CurrentImageTag).HasMaxLength(256);
        builder.Property(x => x.PreviousImageTag).HasMaxLength(256);
        builder.Property(x => x.UpdateChannel).HasMaxLength(32).IsRequired();
        builder.Property(x => x.SetupTokenHash).HasMaxLength(128);
        builder.Ignore(x => x.AwaitingDomain);
    }
}
