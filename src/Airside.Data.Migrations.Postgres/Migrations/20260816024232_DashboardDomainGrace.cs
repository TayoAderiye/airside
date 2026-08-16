using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airside.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class DashboardDomainGrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreviousDashboardDomain",
                table: "instance_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PreviousDashboardDomainUntil",
                table: "instance_settings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviousDashboardDomain",
                table: "instance_settings");

            migrationBuilder.DropColumn(
                name: "PreviousDashboardDomainUntil",
                table: "instance_settings");
        }
    }
}
