using Airside.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Airside.Data.Configurations;

public class NotificationChannelConfiguration : IEntityTypeConfiguration<NotificationChannel>
{
    public void Configure(EntityTypeBuilder<NotificationChannel> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("notification_channels");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.Endpoint).HasMaxLength(1024).IsRequired();
        builder.Property(x => x.MinimumSeverity).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.LastAttemptError).HasMaxLength(512);
        builder.Property(x => x.SettingsJson).IsRequired();
        builder.Property(x => x.RoutingJson).IsRequired();

        builder.HasIndex(x => x.Name).IsUnique();
        builder.HasQueryFilter(x => x.DeletedAt == null);
    }
}

public class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("notification_deliveries");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.LastError).HasMaxLength(512);
        builder.Property(x => x.SkipReason).HasMaxLength(256);

        // One attempt record per notification per channel. The unique index is
        // what makes "has this already been offered here" a lookup rather than a
        // scan, and stops a restart mid-dispatch creating a second row that would
        // deliver the same alert twice.
        builder.HasIndex(x => new { x.NotificationId, x.ChannelId }).IsUnique();

        // The dispatcher's own query: what is due, oldest first.
        builder.HasIndex(x => new { x.Status, x.NextAttemptAt });
    }
}
