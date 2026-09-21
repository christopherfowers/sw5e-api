using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Sw5e.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds <c>class</c> and <c>class-improvement</c> to the seeded content
    /// type table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table is seeded from <c>ContentTypeRegistry</c> rather than by the
    /// importer, so that the foreign key from <c>content_item</c> exists from
    /// the moment the schema does. Adding a type therefore needs a migration,
    /// which is the intended friction: the registry is also what guards the
    /// <c>{type}</c> route value before it reaches a path join.
    /// </para>
    /// <para>
    /// The two new rows are inserted in the middle of the navigation order
    /// rather than appended, because that order is what the site's header
    /// reads and a class belongs beside the archetypes that branch off it.
    /// That is why every row after it has its <c>sort_order</c> moved along by
    /// two: nothing about those rows has changed except where they sit, and
    /// <c>Down</c> puts them back.
    /// </para>
    /// </remarks>
    public partial class RegisterClassContentTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "archetype",
                column: "sort_order",
                value: 5);

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
                keyValue: "equipment",
                column: "sort_order",
                value: 15);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "feat",
                column: "sort_order",
                value: 7);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "feature",
                column: "sort_order",
                value: 6);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "fighting-mastery",
                column: "sort_order",
                value: 11);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "fighting-style",
                column: "sort_order",
                value: 10);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "lightsaber-form",
                column: "sort_order",
                value: 12);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "maneuver",
                column: "sort_order",
                value: 9);

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
                keyValue: "power",
                column: "sort_order",
                value: 8);

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

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "weapon-focus",
                column: "sort_order",
                value: 13);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "weapon-supremacy",
                column: "sort_order",
                value: 14);

            migrationBuilder.InsertData(
                schema: "content",
                table: "content_type",
                columns: new[] { "key", "display_name", "plural_name", "route_segment", "sort_order" },
                values: new object[,]
                {
                    { "class", "Class", "Classes", "classes", 3 },
                    { "class-improvement", "Class improvement", "Class improvements", "class-improvements", 4 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "class");

            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "class-improvement");

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "archetype",
                column: "sort_order",
                value: 3);

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

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "equipment",
                column: "sort_order",
                value: 13);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "feat",
                column: "sort_order",
                value: 5);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "feature",
                column: "sort_order",
                value: 4);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "fighting-mastery",
                column: "sort_order",
                value: 9);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "fighting-style",
                column: "sort_order",
                value: 8);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "lightsaber-form",
                column: "sort_order",
                value: 10);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "maneuver",
                column: "sort_order",
                value: 7);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "monster",
                column: "sort_order",
                value: 14);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "power",
                column: "sort_order",
                value: 6);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-base-size",
                column: "sort_order",
                value: 15);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-deployment",
                column: "sort_order",
                value: 16);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-equipment",
                column: "sort_order",
                value: 17);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-modification",
                column: "sort_order",
                value: 18);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-rule",
                column: "sort_order",
                value: 20);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "starship-venture",
                column: "sort_order",
                value: 19);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "weapon-focus",
                column: "sort_order",
                value: 11);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "weapon-supremacy",
                column: "sort_order",
                value: 12);
        }
    }
}
