using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gazetteer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "location_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_location_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "location_group_members",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    boost = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_location_group_members", x => new { x.group_id, x.location_type });
                    table.ForeignKey(
                        name: "FK_location_group_members_location_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "location_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_location_group_members_group_rank",
                table: "location_group_members",
                columns: new[] { "group_id", "rank" });

            migrationBuilder.CreateIndex(
                name: "ix_location_groups_name",
                table: "location_groups",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "location_group_members");

            migrationBuilder.DropTable(
                name: "location_groups");
        }
    }
}
