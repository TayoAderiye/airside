using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airside.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class NotificationRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SkipReason",
                table: "notification_deliveries",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoutingJson",
                table: "notification_channels",
                type: "text",
                nullable: false,

                // "{}" rather than "". An empty rule set means "send everything",
                // which is what every channel created before routing existed was
                // already doing — so adding the column changes nothing for them.
                // An empty string parses to the same thing today, but relying on
                // that would make the safety of this migration depend on a
                // fallback rather than on the value stored.
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SkipReason",
                table: "notification_deliveries");

            migrationBuilder.DropColumn(
                name: "RoutingJson",
                table: "notification_channels");
        }
    }
}
