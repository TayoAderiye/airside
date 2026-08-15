using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airside.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Databases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workloads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HostId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Slug = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StateChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CpuLimitNanos = table.Column<long>(type: "bigint", nullable: false),
                    MemoryLimitBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageAllocationBytes = table.Column<long>(type: "bigint", nullable: false),
                    AutoRestart = table.Column<bool>(type: "boolean", nullable: false),
                    ContainerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    NetworkId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    NetworkName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ActiveJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastReconciledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DriftState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Engine = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ImageRef = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ImageDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DatabaseName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PublishedPort = table.Column<int>(type: "integer", nullable: true),
                    PublishBindAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    MaxMemoryBytes = table.Column<long>(type: "bigint", nullable: true),
                    MaxMemoryPolicy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    AofEnabled = table.Column<bool>(type: "boolean", nullable: true),
                    BackupEnabled = table.Column<bool>(type: "boolean", nullable: true),
                    BackupCron = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BackupRetentionCount = table.Column<int>(type: "integer", nullable: true),
                    BackupRetentionDays = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workloads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workloads_hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "database_credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DatabaseInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EncryptedPassword = table.Column<string>(type: "text", nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SupersededAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RotatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_database_credentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_database_credentials_workloads_DatabaseInstanceId",
                        column: x => x.DatabaseInstanceId,
                        principalTable: "workloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "volumes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HostId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkloadId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    MountPath = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SizeAllocationBytes = table.Column<long>(type: "bigint", nullable: false),
                    LastMeasuredBytes = table.Column<long>(type: "bigint", nullable: true),
                    MeasuredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OrphanedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volumes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_volumes_workloads_WorkloadId",
                        column: x => x.WorkloadId,
                        principalTable: "workloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_database_credentials_DatabaseInstanceId_State",
                table: "database_credentials",
                columns: new[] { "DatabaseInstanceId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_volumes_Name",
                table: "volumes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_volumes_OrphanedAt",
                table: "volumes",
                column: "OrphanedAt");

            migrationBuilder.CreateIndex(
                name: "IX_volumes_WorkloadId",
                table: "volumes",
                column: "WorkloadId");

            migrationBuilder.CreateIndex(
                name: "IX_workloads_DeletedAt",
                table: "workloads",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_workloads_HostId_Slug",
                table: "workloads",
                columns: new[] { "HostId", "Slug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "database_credentials");

            migrationBuilder.DropTable(
                name: "volumes");

            migrationBuilder.DropTable(
                name: "workloads");
        }
    }
}
