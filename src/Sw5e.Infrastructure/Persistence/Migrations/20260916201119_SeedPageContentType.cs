using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sw5e.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the type that holds a built page's own words.
    /// </summary>
    /// <remarks>
    /// The front page's hero paragraph and its three section headings were
    /// markup, so nobody could change a word of them without a code edit and a
    /// deploy. They are documents now, one per page, with every slot optional
    /// and the built-in wording as the fallback. Appended, so no existing sort
    /// order moves.
    /// </remarks>
    public partial class SeedPageContentType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "content",
                table: "content_type",
                columns: new[] { "key", "display_name", "plural_name", "route_segment", "sort_order" },
                values: new object[] { "page", "Page", "Pages", "pages", 33 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "content",
                table: "content_type",
                keyColumn: "key",
                keyValue: "page");
        }
    }
}
