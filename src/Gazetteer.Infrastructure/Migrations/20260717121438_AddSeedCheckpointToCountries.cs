using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gazetteer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSeedCheckpointToCountries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "last_seeded_at_utc",
                table: "countries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_seeded_file_timestamp_utc",
                table: "countries",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_seeded_at_utc",
                table: "countries");

            migrationBuilder.DropColumn(
                name: "last_seeded_file_timestamp_utc",
                table: "countries");
        }
    }
}
