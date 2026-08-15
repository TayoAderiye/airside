using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airside.Data.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class DatabaseOps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DatabaseInstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TriggerKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StoragePath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    EngineSnapshot = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DatabaseNameSnapshot = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsRetained = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_backups_workloads_DatabaseInstanceId",
                        column: x => x.DatabaseInstanceId,
                        principalTable: "workloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "query_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DatabaseInstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: false),
                    ExecutedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    RowsAffected = table.Column<int>(type: "INTEGER", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_query_history", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "saved_queries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DatabaseInstanceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_queries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "restores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DatabaseInstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BackupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PreRestoreBackupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_restores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_restores_backups_BackupId",
                        column: x => x.BackupId,
                        principalTable: "backups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_restores_workloads_DatabaseInstanceId",
                        column: x => x.DatabaseInstanceId,
                        principalTable: "workloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_backups_DatabaseInstanceId_StartedAt",
                table: "backups",
                columns: new[] { "DatabaseInstanceId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_backups_ExpiresAt",
                table: "backups",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_query_history_UserId_DatabaseInstanceId_ExecutedAt",
                table: "query_history",
                columns: new[] { "UserId", "DatabaseInstanceId", "ExecutedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_restores_BackupId",
                table: "restores",
                column: "BackupId");

            migrationBuilder.CreateIndex(
                name: "IX_restores_DatabaseInstanceId_StartedAt",
                table: "restores",
                columns: new[] { "DatabaseInstanceId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_saved_queries_UserId_Name",
                table: "saved_queries",
                columns: new[] { "UserId", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "query_history");

            migrationBuilder.DropTable(
                name: "restores");

            migrationBuilder.DropTable(
                name: "saved_queries");

            migrationBuilder.DropTable(
                name: "backups");
        }
    }
}
