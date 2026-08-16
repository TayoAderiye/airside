using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airside.Data.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class NotificationChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    EncryptedSecret = table.Column<string>(type: "TEXT", nullable: true),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    MinimumSeverity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastAttemptSucceeded = table.Column<bool>(type: "INTEGER", nullable: true),
                    LastAttemptError = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    MutedUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_channels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    NotificationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_deliveries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_channels_Name",
                table: "notification_channels",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_NotificationId_ChannelId",
                table: "notification_deliveries",
                columns: new[] { "NotificationId", "ChannelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_Status_NextAttemptAt",
                table: "notification_deliveries",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_channels");

            migrationBuilder.DropTable(
                name: "notification_deliveries");
        }
    }
}
