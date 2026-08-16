using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airside.Data.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ImageVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImageVariant",
                table: "workloads",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UsesCustomImage",
                table: "workloads",
                type: "boolean",
                nullable: true);

            // Existing rows predate the column and would otherwise describe
            // themselves as a variant they were never built on. The tag each
            // engine resolved to before this migration is known exactly, so the
            // backfill is a fact rather than a guess: Postgres and Redis always
            // resolved to -alpine, MySQL and MongoDB to the unsuffixed Debian tag.
            migrationBuilder.Sql(
                "UPDATE workloads SET \"ImageVariant\" = 'Alpine' " +
                "WHERE \"ImageVariant\" IS NULL AND \"Engine\" IN ('Postgres', 'Redis')");

            migrationBuilder.Sql(
                "UPDATE workloads SET \"ImageVariant\" = 'Debian' " +
                "WHERE \"ImageVariant\" IS NULL AND \"Engine\" IN ('MySql', 'MongoDb')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImageVariant",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "UsesCustomImage",
                table: "workloads");
        }
    }
}
