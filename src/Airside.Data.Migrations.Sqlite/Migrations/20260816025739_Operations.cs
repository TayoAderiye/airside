using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airside.Data.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class Operations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "metric_rollups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkloadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HourUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SampleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CpuNanosAvg = table.Column<long>(type: "INTEGER", nullable: false),
                    CpuNanosMax = table.Column<long>(type: "INTEGER", nullable: false),
                    CpuLimitNanos = table.Column<long>(type: "INTEGER", nullable: false),
                    MemoryBytesAvg = table.Column<long>(type: "INTEGER", nullable: false),
                    MemoryBytesMax = table.Column<long>(type: "INTEGER", nullable: false),
                    MemoryLimitBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    NetworkRxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    NetworkTxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metric_rollups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DedupeKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ResourceKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OccurrenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AcknowledgedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "update_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ToVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FromImageDigest = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ToImageDigest = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    AppliedMigrations = table.Column<bool>(type: "INTEGER", nullable: false),
                    PreUpdateBackupPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    StartedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_update_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "user_mfa",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EncryptedSecret = table.Column<string>(type: "TEXT", nullable: false),
                    RecoveryCodeHashes = table.Column<string>(type: "TEXT", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUsedTimeStep = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_mfa", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_metric_rollups_HourUtc",
                table: "metric_rollups",
                column: "HourUtc");

            migrationBuilder.CreateIndex(
                name: "IX_metric_rollups_WorkloadId_HourUtc",
                table: "metric_rollups",
                columns: new[] { "WorkloadId", "HourUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_DedupeKey_ResolvedAt",
                table: "notifications",
                columns: new[] { "DedupeKey", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_LastSeenAt",
                table: "notifications",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_update_records_StartedAt",
                table: "update_records",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_user_mfa_UserId",
                table: "user_mfa",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "metric_rollups");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "update_records");

            migrationBuilder.DropTable(
                name: "user_mfa");
        }
    }
}
