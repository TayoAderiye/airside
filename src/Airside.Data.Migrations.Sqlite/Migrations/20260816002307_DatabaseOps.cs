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
            migrationBuilder.RenameColumn(
                name: "SupersededAt",
                table: "database_credentials",
                newName: "RetiredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "RetiredAt",
                table: "database_credentials",
                newName: "SupersededAt");
        }
    }
}
