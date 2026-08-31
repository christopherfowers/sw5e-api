using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Sw5e.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the six combat-option types — maneuvers, fighting styles, fighting
    /// masteries, lightsaber forms, weapon focuses and weapon supremacies — to
    /// the seeded type registry.
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
    /// The two <c>UpdateData</c> calls are not incidental. <c>sort_order</c> is
    /// the type's position in the registry, and the combat options belong after
    /// powers and before equipment — that is the order a character is built in
    /// and the order the site's navigation shows. Appending them to the end
    /// instead would have avoided touching equipment and monsters, and would
    /// have left the seeded order disagreeing with the compiled one, which is
    /// exactly what <c>Migrate_SeedsTheTypeRegistryFromTheCompiledOne</c>
    /// exists to catch.
    /// </para>
    /// </remarks>
    public partial class SeedCombatOptionContentTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                keyValue: "monster",
                column: "sort_order",
                value: 14);

            migrationBuilder.InsertData(
                schema: "content",
                table: "content_type",
                columns: new[] { "key", "display_name", "plural_name", "route_segment", "sort_order" },
                values: new object[,]
                {
                    { "fighting-mastery", "Fighting Mastery", "Fighting Masteries", "fighting-masteries", 9 },
                    { "fighting-style", "Fighting Style", "Fighting Styles", "fighting-styles", 8 },
                    { "lightsaber-form", "Lightsaber Form", "Lightsaber Forms", "lightsaber-forms", 10 },
                    { "maneuver", "Maneuver", "Maneuvers", "maneuvers", 7 },
                    { "weapon-focus", "Weapon Focus", "Weapon Focuses", "weapon-focuses", 11 },
                    { "weapon-supremacy", "Weapon Supremacy", "Weapon Supremacies", "weapon-supremacies", 12 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "fighting-mastery");

            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "fighting-style");

            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "lightsaber-form");

            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "maneuver");

            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "weapon-focus");

            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "weapon-supremacy");

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "equipment",
                column: "sort_order",
                value: 7);

            migrationBuilder.UpdateData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "monster",
                column: "sort_order",
                value: 8);
        }
    }
}
