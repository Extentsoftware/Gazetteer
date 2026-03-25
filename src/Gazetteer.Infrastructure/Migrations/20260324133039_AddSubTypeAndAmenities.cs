using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gazetteer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSubTypeAndAmenities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "sub_type",
                table: "locations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "sub_type",
                table: "locations");
        }
    }
}
