using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace server.core.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalApiRateLimitTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalApiRateLimitBuckets",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BucketKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PausedUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalApiRateLimitBuckets", x => new { x.Provider, x.Operation, x.BucketKey });
                });

            migrationBuilder.CreateTable(
                name: "ExternalApiRateLimitReservations",
                columns: table => new
                {
                    ReservationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Provider = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BucketKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RequestId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Cost = table.Column<int>(type: "int", nullable: false),
                    ReservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalApiRateLimitReservations", x => x.ReservationId);
                    table.CheckConstraint("CK_ExternalApiRateLimitReservations_Cost", "[Cost] > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalApiRateLimitBuckets_PausedUntilUtc",
                table: "ExternalApiRateLimitBuckets",
                column: "PausedUntilUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalApiRateLimitReservations_Provider_Operation_BucketKey_ExpiresAtUtc",
                table: "ExternalApiRateLimitReservations",
                columns: new[] { "Provider", "Operation", "BucketKey", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalApiRateLimitReservations_RequestId",
                table: "ExternalApiRateLimitReservations",
                column: "RequestId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalApiRateLimitBuckets");

            migrationBuilder.DropTable(
                name: "ExternalApiRateLimitReservations");
        }
    }
}
