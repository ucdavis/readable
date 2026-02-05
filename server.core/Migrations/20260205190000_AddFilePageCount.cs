using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace server.core.Migrations
{
    /// <inheritdoc />
    public partial class AddFilePageCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PageCount",
                table: "Files",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PageCount",
                table: "Files");
        }
    }
}
