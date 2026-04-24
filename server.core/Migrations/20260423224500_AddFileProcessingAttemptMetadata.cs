using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using server.core.Data;

#nullable disable

namespace server.core.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260423224500_AddFileProcessingAttemptMetadata")]
    public partial class AddFileProcessingAttemptMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MetadataJson",
                table: "FileProcessingAttempts",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_FileProcessingAttempts_MetadataJson_IsJson",
                table: "FileProcessingAttempts",
                sql: "[MetadataJson] IS NULL OR ISJSON([MetadataJson]) > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_FileProcessingAttempts_MetadataJson_IsJson",
                table: "FileProcessingAttempts");

            migrationBuilder.DropColumn(
                name: "MetadataJson",
                table: "FileProcessingAttempts");
        }
    }
}
