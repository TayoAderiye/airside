using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airside.Data.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class SlugUniquePerLiveWorkload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workloads_HostId_Slug",
                table: "workloads");

            migrationBuilder.CreateIndex(
                name: "IX_workloads_HostId_Slug",
                table: "workloads",
                columns: new[] { "HostId", "Slug" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workloads_HostId_Slug",
                table: "workloads");

            migrationBuilder.CreateIndex(
                name: "IX_workloads_HostId_Slug",
                table: "workloads",
                columns: new[] { "HostId", "Slug" },
                unique: true);
        }
    }
}
