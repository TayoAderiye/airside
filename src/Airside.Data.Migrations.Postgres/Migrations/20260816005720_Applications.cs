using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airside.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Applications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BuildContextPath",
                table: "workloads",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContainerPort",
                table: "workloads",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CurrentDeploymentId",
                table: "workloads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DockerfileContent",
                table: "workloads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DockerfilePath",
                table: "workloads",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitBranch",
                table: "workloads",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GitCredentialId",
                table: "workloads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitRepositoryUrl",
                table: "workloads",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HealthCheckCommandJson",
                table: "workloads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HealthCheckExpectedStatus",
                table: "workloads",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HealthCheckIntervalSeconds",
                table: "workloads",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HealthCheckKind",
                table: "workloads",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HealthCheckPath",
                table: "workloads",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HealthCheckRetries",
                table: "workloads",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HealthCheckTimeoutSeconds",
                table: "workloads",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RegistryCredentialId",
                table: "workloads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceImageRef",
                table: "workloads",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceKind",
                table: "workloads",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "database_attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DatabaseInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvKeyPrefix = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttachedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AttachedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DetachedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DetachedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_database_attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_database_attachments_workloads_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "workloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_database_attachments_workloads_DatabaseInstanceId",
                        column: x => x.DatabaseInstanceId,
                        principalTable: "workloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deployments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TriggerKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceKindSnapshot = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CommitMessage = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Branch = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ImageRef = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ImageDigest = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ContainerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    RolledBackFromDeploymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    TriggeredByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deployments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_deployments_workloads_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "workloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "environment_variables",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "character varying(32768)", maxLength: 32768, nullable: false),
                    IsSecret = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environment_variables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_environment_variables_workloads_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "workloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "source_credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsRegistry = table.Column<bool>(type: "boolean", nullable: false),
                    Username = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    EncryptedSecret = table.Column<string>(type: "text", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_credentials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "deployment_logs",
                columns: table => new
                {
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Truncated = table.Column<bool>(type: "boolean", nullable: false),
                    ByteCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deployment_logs", x => x.DeploymentId);
                    table.ForeignKey(
                        name: "FK_deployment_logs_deployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "deployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_database_attachments_ApplicationId_DatabaseInstanceId_Detac~",
                table: "database_attachments",
                columns: new[] { "ApplicationId", "DatabaseInstanceId", "DetachedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_database_attachments_ApplicationId_EnvKeyPrefix_DetachedAt",
                table: "database_attachments",
                columns: new[] { "ApplicationId", "EnvKeyPrefix", "DetachedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_database_attachments_DatabaseInstanceId",
                table: "database_attachments",
                column: "DatabaseInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_deployments_ApplicationId_Number",
                table: "deployments",
                columns: new[] { "ApplicationId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_deployments_ApplicationId_StartedAt",
                table: "deployments",
                columns: new[] { "ApplicationId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_environment_variables_ApplicationId_Key",
                table: "environment_variables",
                columns: new[] { "ApplicationId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_source_credentials_Name",
                table: "source_credentials",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "database_attachments");

            migrationBuilder.DropTable(
                name: "deployment_logs");

            migrationBuilder.DropTable(
                name: "environment_variables");

            migrationBuilder.DropTable(
                name: "source_credentials");

            migrationBuilder.DropTable(
                name: "deployments");

            migrationBuilder.DropColumn(
                name: "BuildContextPath",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "ContainerPort",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "CurrentDeploymentId",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "DockerfileContent",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "DockerfilePath",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "GitBranch",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "GitCredentialId",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "GitRepositoryUrl",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "HealthCheckCommandJson",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "HealthCheckExpectedStatus",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "HealthCheckIntervalSeconds",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "HealthCheckKind",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "HealthCheckPath",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "HealthCheckRetries",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "HealthCheckTimeoutSeconds",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "RegistryCredentialId",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "SourceImageRef",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "SourceKind",
                table: "workloads");
        }
    }
}
