using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airside.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class RegistryCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "registry_credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Registry = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EncryptedPassword = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastVerificationSucceeded = table.Column<bool>(type: "boolean", nullable: true),
                    LastVerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastVerificationError = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_registry_credentials", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_registry_credentials_Registry",
                table: "registry_credentials",
                column: "Registry",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "registry_credentials");
        }
    }
}
