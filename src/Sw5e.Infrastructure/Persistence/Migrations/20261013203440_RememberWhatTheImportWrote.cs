using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sw5e.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RememberWhatTheImportWrote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "imported_version",
                schema: "content",
                table: "content_item",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                collation: "C");

        /*
          Every row already here was written by the importer, so the column is
          backfilled from the version rather than left null.

          The exception is the handful somebody had published, which an import
          has already overwritten by the time this runs: for those, version is
          what the import last wrote, which is exactly what this column means.
          Either way the next import reads the truth.
        */
        migrationBuilder.Sql(
            """
            UPDATE content.content_item
            SET imported_version = version
            WHERE imported_version IS NULL;
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "imported_version",
                schema: "content",
                table: "content_item");
        }
    }
}
