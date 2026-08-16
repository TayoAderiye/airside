using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airside.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Domains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "domains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RouteId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CertificateIssuer = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CertificateNotBefore = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CertificateNotAfter = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CertificateAutoRenew = table.Column<bool>(type: "boolean", nullable: false),
                    LastCertificateCheckAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_domains", x => x.Id);
                    table.ForeignKey(
                        name: "FK_domains_workloads_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "workloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_domains_ApplicationId",
                table: "domains",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_domains_Hostname",
                table: "domains",
                column: "Hostname",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "domains");
        }
    }
}
