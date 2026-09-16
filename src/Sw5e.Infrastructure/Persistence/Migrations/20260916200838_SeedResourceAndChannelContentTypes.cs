using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Sw5e.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the hosted files and the community's outbound links to the seeded
    /// type registry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both already had a published schema and documents in the content
    /// repository, and the site's build already read them. What they did not
    /// have was a row here, so the API would not serve either and the authoring
    /// screens could not list them. An administrator could see the two shelves
    /// on the front page and had no way to reorder, rename or add to them.
    /// </para>
    /// <para>
    /// Appended rather than slotted in, so no existing sort order moves. These
    /// are site furniture rather than game content and the navigation does not
    /// show them, which is the same position the attribution types are in.
    /// </para>
    /// </remarks>
    public partial class SeedResourceAndChannelContentTypes : Migration
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
                    { "channel", "Channel", "Channels", "channels", 32 },
                    { "resource", "Resource", "Resources", "resources", 31 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "channel");

            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "resource");
        }
    }
}
