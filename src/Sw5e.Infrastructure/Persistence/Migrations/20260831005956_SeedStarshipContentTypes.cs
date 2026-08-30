using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Sw5e.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// The three UpdateData calls are not incidental. `sort_order` is the
    /// registry's own index, and the starship types are game content that
    /// belongs in the site's navigation between the creatures and the
    /// attribution types, which are not. Appending them after the credits
    /// instead would have avoided renumbering three rows at the cost of
    /// putting the credits in the middle of the navigation, so the rows move.
    /// The change is reversible and Down puts them back.
    /// </remarks>
    public partial class SeedStarshipContentTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "asset-credit",
                column: "sort_order",
                value: 23);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "credit",
                column: "sort_order",
                value: 22);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "credit-category",
                column: "sort_order",
                value: 21);

            migrationBuilder.InsertData(
                schema: "content",
                table: "content_type",
                columns: new[] { "key", "display_name", "plural_name", "route_segment", "sort_order" },
                values: new object[,]
                {
                    { "starship-base-size", "Starship Base Size", "Starship Base Sizes", "starship-base-sizes", 15 },
                    { "starship-deployment", "Starship Deployment", "Starship Deployments", "starship-deployments", 16 },
                    { "starship-equipment", "Starship Equipment", "Starship Equipment", "starship-equipment", 17 },
                    { "starship-modification", "Starship Modification", "Starship Modifications", "starship-modifications", 18 },
                    { "starship-rule", "Starship Rule", "Starship Rules", "starship-rules", 20 },
                    { "starship-venture", "Starship Venture", "Starship Ventures", "starship-ventures", 19 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-base-size");

            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-deployment");

            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-equipment");

            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-modification");

            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-rule");

            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-venture");

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "asset-credit",
                column: "sort_order",
                value: 17);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "credit",
                column: "sort_order",
                value: 16);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "credit-category",
                column: "sort_order",
                value: 15);
        }
    }
}
