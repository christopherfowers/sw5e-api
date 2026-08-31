using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Sw5e.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeedAttributionContentTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "content",
                table: "content_type",
                columns: new[] { "key", "display_name", "plural_name", "route_segment", "sort_order" },
                values: new object[,]
                {
                    { "asset-credit", "Asset credit", "Asset credits", "asset-credits", 17 },
                    { "credit", "Credit", "Credits", "credits", 16 },
                    { "credit-category", "Credit category", "Credit categories", "credit-categories", 15 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "asset-credit");

            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "credit");

            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "credit-category");
        }
    }
}
