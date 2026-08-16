using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airside.Data.Migrations.Sqlite.Migrations
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RouteId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CertificateIssuer = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CertificateNotBefore = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CertificateNotAfter = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CertificateAutoRenew = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastCertificateCheckAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<Guid>(type: "TEXT", nullable: false)
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
