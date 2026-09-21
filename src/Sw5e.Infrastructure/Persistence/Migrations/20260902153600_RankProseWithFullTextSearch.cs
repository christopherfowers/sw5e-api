using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Sw5e.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RankProseWithFullTextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                schema: "content",
                table: "content_item",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "setweight(to_tsvector('english', name_lower), 'A') ||\r\nsetweight(to_tsvector('english', heading_text_lower), 'B') ||\r\nsetweight(to_tsvector('english', search_text_lower), 'D')",
                stored: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "search_vector",
                schema: "content",
                table: "content_item");
        }
    }
}
