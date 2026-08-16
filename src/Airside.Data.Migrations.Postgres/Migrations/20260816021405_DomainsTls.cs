using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airside.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class DomainsTls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-corrected. EF inferred this as a rename of "State" to "TlsMode",
            // which would have written lifecycle values — Pending, Active, Failed —
            // into the column that decides how a certificate is obtained. The old
            // "State" is the same concept as the new "Status", so that is the
            // rename; TlsMode is a genuinely new column.
            migrationBuilder.RenameColumn(
                name: "State",
                table: "domains",
                newName: "Status");

            migrationBuilder.AddColumn<string>(
                name: "CertificateFingerprint",
                table: "domains",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CertificateIsStaging",
                table: "domains",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CertificateSans",
                table: "domains",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CertificateSecretId",
                table: "domains",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CertificateSubject",
                table: "domains",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DetachedAt",
                table: "domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayHostname",
                table: "domains",
                type: "character varying(253)",
                maxLength: 253,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                table: "domains",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HstsEnabled",
                table: "domains",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HstsIncludeSubdomains",
                table: "domains",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "HstsMaxAgeSeconds",
                table: "domains",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "HstsPreload",
                table: "domains",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastValidationAt",
                table: "domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastValidationJson",
                table: "domains",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RedirectToDomainId",
                table: "domains",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegisteredDomain",
                table: "domains",
                type: "character varying(253)",
                maxLength: 253,
                nullable: false,
                defaultValue: "");

            // Every domain that exists predates the TlsMode field, and the only
            // behaviour the old code had was Caddy's automatic HTTPS — so that is
            // what those rows were actually doing.
            migrationBuilder.AddColumn<string>(
                name: "TlsMode",
                table: "domains",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Automatic");

            migrationBuilder.CreateTable(
                name: "issuance_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    RegisteredDomain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    Staging = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RetryAfter = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttemptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_issuance_attempts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_domains_CertificateNotAfter",
                table: "domains",
                column: "CertificateNotAfter");

            migrationBuilder.CreateIndex(
                name: "IX_domains_RedirectToDomainId",
                table: "domains",
                column: "RedirectToDomainId");

            migrationBuilder.CreateIndex(
                name: "IX_domains_RegisteredDomain",
                table: "domains",
                column: "RegisteredDomain");

            migrationBuilder.CreateIndex(
                name: "IX_issuance_attempts_Hostname_AttemptedAt",
                table: "issuance_attempts",
                columns: new[] { "Hostname", "AttemptedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_issuance_attempts_RegisteredDomain_AttemptedAt",
                table: "issuance_attempts",
                columns: new[] { "RegisteredDomain", "AttemptedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_domains_domains_RedirectToDomainId",
                table: "domains",
                column: "RedirectToDomainId",
                principalTable: "domains",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_domains_domains_RedirectToDomainId",
                table: "domains");

            migrationBuilder.DropTable(
                name: "issuance_attempts");

            migrationBuilder.DropIndex(
                name: "IX_domains_CertificateNotAfter",
                table: "domains");

            migrationBuilder.DropIndex(
                name: "IX_domains_RedirectToDomainId",
                table: "domains");

            migrationBuilder.DropIndex(
                name: "IX_domains_RegisteredDomain",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "CertificateFingerprint",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "CertificateIsStaging",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "CertificateSans",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "CertificateSecretId",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "CertificateSubject",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "DetachedAt",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "DisplayHostname",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "ErrorMessage",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "HstsEnabled",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "HstsIncludeSubdomains",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "HstsMaxAgeSeconds",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "HstsPreload",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "LastValidationAt",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "LastValidationJson",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "RedirectToDomainId",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "RegisteredDomain",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "TlsMode",
                table: "domains");

            migrationBuilder.RenameColumn(
                name: "Status",
                table: "domains",
                newName: "State");
        }
    }
}
