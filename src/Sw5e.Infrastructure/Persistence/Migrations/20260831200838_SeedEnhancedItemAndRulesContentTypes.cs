using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Sw5e.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the enhanced items, the two property glossaries, the rules prose
    /// and the reference tables to the seeded type registry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>content_type</c> is seeded from the compiled
    /// <c>ContentTypeRegistry</c> rather than by the importer, so that
    /// <c>content_item.content_type</c> has something to point its foreign key
    /// at from the moment the schema exists. That is why adding a type needs a
    /// migration at all, and the friction is deliberate: the registry is also
    /// what stands between a <c>{type}</c> route value and a path join.
    /// </para>
    /// <para>
    /// The <c>UpdateData</c> calls are not incidental. <c>sort_order</c> is the
    /// type's position in the registry, and three of the five new types belong
    /// beside the gear they qualify rather than after everything: enhanced
    /// items next to the equipment they are the enhanced form of, and the two
    /// glossaries next to both, because that is where a reader looking at
    /// "burst 2" on a weapon row goes next. Everything after them shifts down.
    /// </para>
    /// </remarks>
    public partial class SeedEnhancedItemAndRulesContentTypes : Migration
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
                value: 30);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "credit",
                column: "sort_order",
                value: 29);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "credit-category",
                column: "sort_order",
                value: 28);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "monster",
                column: "sort_order",
                value: 19);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-base-size",
                column: "sort_order",
                value: 20);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-deployment",
                column: "sort_order",
                value: 21);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-equipment",
                column: "sort_order",
                value: 22);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-modification",
                column: "sort_order",
                value: 23);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-rule",
                column: "sort_order",
                value: 25);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-venture",
                column: "sort_order",
                value: 24);

            migrationBuilder.InsertData(
                schema: "content",
                table: "content_type",
                columns: new[] { "key", "display_name", "plural_name", "route_segment", "sort_order" },
                values: new object[,]
                {
                    { "armor-property", "Armor property", "Armor properties", "armor-properties", 18 },
                    { "enhanced-item", "Enhanced item", "Enhanced items", "enhanced-items", 16 },
                    { "reference-table", "Reference table", "Reference tables", "reference-tables", 27 },
                    { "rule", "Rule", "Rules", "rules", 26 },
                    { "weapon-property", "Weapon property", "Weapon properties", "weapon-properties", 17 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "armor-property");

            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "enhanced-item");

            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "reference-table");

            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "rule");

            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "weapon-property");

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "asset-credit",
                column: "sort_order",
                value: 25);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "credit",
                column: "sort_order",
                value: 24);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "credit-category",
                column: "sort_order",
                value: 23);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "monster",
                column: "sort_order",
                value: 16);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-base-size",
                column: "sort_order",
                value: 17);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-deployment",
                column: "sort_order",
                value: 18);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-equipment",
                column: "sort_order",
                value: 19);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-modification",
                column: "sort_order",
                value: 20);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-rule",
                column: "sort_order",
                value: 22);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-venture",
                column: "sort_order",
                value: 21);
        }
    }
}
