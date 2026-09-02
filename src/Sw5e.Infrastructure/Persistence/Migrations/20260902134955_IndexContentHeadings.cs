using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sw5e.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IndexContentHeadings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "heading_text_lower",
                schema: "content",
                table: "content_item",
                type: "text",
                nullable: false,
                defaultValue: "",
                collation: "C");

            migrationBuilder.CreateIndex(
                name: "ix_content_item_heading_text_trgm",
                schema: "content",
                table: "content_item",
                column: "heading_text_lower")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_content_item_heading_text_trgm",
                schema: "content",
                table: "content_item");

            migrationBuilder.DropColumn(
                name: "heading_text_lower",
                schema: "content",
                table: "content_item");
        }
    }
}
